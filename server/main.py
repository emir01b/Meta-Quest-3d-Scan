"""
MetaScan — Main Server
FastAPI + WebSocket server that receives data from Quest 3S
and orchestrates 3D reconstruction.
"""
import asyncio
import json
import logging
import time
import uuid
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, WebSocket, WebSocketDisconnect, HTTPException
from fastapi.staticfiles import StaticFiles
from fastapi.responses import JSONResponse
from fastapi.middleware.cors import CORSMiddleware

from config import (
    SERVER_HOST, SERVER_PORT, SESSIONS_DIR, EXPORTS_DIR,
    MIN_FRAMES_FOR_RECONSTRUCTION
)
from frame_handler import FrameHandler
from reconstruction import ReconstructionPipeline, SimpleReconstruction

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(name)s] %(levelname)s: %(message)s",
    datefmt="%H:%M:%S",
)
logger = logging.getLogger("metascan.server")

# Active sessions
active_sessions: dict[str, dict] = {}
reconstruction_tasks: dict[str, asyncio.Task] = {}


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Application lifespan handler."""
    logger.info("=" * 60)
    logger.info("  MetaScan Server Starting")
    logger.info(f"  WebSocket: ws://{SERVER_HOST}:{SERVER_PORT}/ws/scan")
    logger.info(f"  API: http://{SERVER_HOST}:{SERVER_PORT}/api")
    logger.info(f"  Viewer: http://{SERVER_HOST}:{SERVER_PORT}/viewer")
    logger.info("=" * 60)
    yield
    for task in reconstruction_tasks.values():
        task.cancel()
    logger.info("Server shutting down")


app = FastAPI(
    title="MetaScan Server",
    description="3D object scanning and reconstruction from Meta Quest 3S",
    version="2.0.0",
    lifespan=lifespan,
)

# CORS for web viewer
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Serve viewer static files
viewer_dir = Path(__file__).parent.parent / "viewer"
if viewer_dir.exists():
    app.mount("/viewer", StaticFiles(directory=str(viewer_dir), html=True), name="viewer")

# Serve exported models
if EXPORTS_DIR.exists():
    app.mount("/exports", StaticFiles(directory=str(EXPORTS_DIR)), name="exports")


# =============================================================================
# REST API Endpoints
# =============================================================================

@app.get("/api/health")
async def health_check():
    """Health check endpoint."""
    return {"status": "ok", "version": "2.0.0", "timestamp": time.time()}


@app.get("/api/sessions")
async def list_sessions():
    """List all scanning sessions."""
    sessions = []
    if SESSIONS_DIR.exists():
        for session_dir in sorted(SESSIONS_DIR.iterdir(), reverse=True):
            if not session_dir.is_dir():
                continue
            meta_file = session_dir / "session_metadata.json"
            if meta_file.exists():
                with open(meta_file) as f:
                    meta = json.load(f)
                sessions.append({
                    "session_id": session_dir.name,
                    "frame_count": meta.get("total_frames", 0),
                    "timestamp": meta.get("timestamp", 0),
                    "duration": meta.get("duration_seconds", 0),
                })
            else:
                images_dir = session_dir / "images"
                image_count = 0
                if images_dir.exists():
                    image_count = len(list(images_dir.glob("*.jpg")))
                sessions.append({
                    "session_id": session_dir.name,
                    "frame_count": image_count,
                })
    return {"sessions": sessions}


@app.get("/api/sessions/{session_id}")
async def get_session(session_id: str):
    """Get session details."""
    session_dir = SESSIONS_DIR / session_id
    if not session_dir.exists():
        raise HTTPException(status_code=404, detail="Session not found")

    meta_file = session_dir / "session_metadata.json"
    if meta_file.exists():
        with open(meta_file) as f:
            return json.load(f)

    images_dir = session_dir / "images"
    image_count = 0
    if images_dir.exists():
        image_count = len(list(images_dir.glob("*.jpg")))
    return {"session_id": session_id, "frame_count": image_count}


@app.post("/api/sessions/{session_id}/reconstruct")
async def start_reconstruction(session_id: str, method: str = "full"):
    """
    Start 3D reconstruction for a session.
    method: 'colmap', 'full' (both), or 'tsdf' (depth-only fallback)
    """
    session_dir = SESSIONS_DIR / session_id
    if not session_dir.exists():
        raise HTTPException(status_code=404, detail="Session not found")

    if session_id in reconstruction_tasks:
        task = reconstruction_tasks[session_id]
        if not task.done():
            return {"status": "already_running", "session_id": session_id}

    async def run_reconstruction():
        loop = asyncio.get_event_loop()
        pipeline = ReconstructionPipeline(session_dir, session_id)

        if method == "tsdf":
            result = await loop.run_in_executor(
                None, SimpleReconstruction.reconstruct_from_depth_maps,
                session_dir, session_id,
            )
        elif method == "colmap":
            result = await loop.run_in_executor(
                None, pipeline.run_colmap_pipeline, True,
            )
        else:  # full
            result = await loop.run_in_executor(
                None, pipeline.run_full_pipeline, True,
            )

        logger.info(
            f"Reconstruction complete for {session_id}: "
            f"{result.get('status', 'unknown')}"
        )
        return result

    task = asyncio.create_task(run_reconstruction())
    reconstruction_tasks[session_id] = task

    return {"status": "started", "session_id": session_id, "method": method}


@app.get("/api/sessions/{session_id}/reconstruction/status")
async def reconstruction_status(session_id: str):
    """Check reconstruction status."""
    if session_id not in reconstruction_tasks:
        return {"status": "not_started"}

    task = reconstruction_tasks[session_id]
    if task.done():
        try:
            result = task.result()
            return {"status": "completed", "result": result}
        except Exception as e:
            return {"status": "failed", "error": str(e)}
    else:
        return {"status": "running"}


@app.get("/api/sessions/{session_id}/exports")
async def list_exports(session_id: str):
    """List exported files for a session."""
    export_dir = EXPORTS_DIR / session_id
    if not export_dir.exists():
        return {"exports": []}

    exports = []
    for f in export_dir.iterdir():
        if f.is_file():
            exports.append({
                "filename": f.name,
                "format": f.suffix.lstrip(".").upper(),
                "size_mb": round(f.stat().st_size / (1024 * 1024), 2),
                "url": f"/exports/{session_id}/{f.name}",
            })
    return {"exports": exports}


# =============================================================================
# WebSocket — Real-time Frame Streaming
# =============================================================================

@app.websocket("/ws/scan")
async def websocket_scan(websocket: WebSocket):
    """
    WebSocket endpoint for real-time frame streaming from Quest 3S.

    Protocol:
    1. Client sends JSON: {"action": "start_session", "name": "my_scan"}
    2. Server responds: {"action": "session_started", "session_id": "..."}
    3. Client sends binary frames (see FrameHandler for format)
    4. Server responds with quality feedback per frame
    5. Client sends JSON: {"action": "stop_session"}
    6. Server finalizes and optionally starts reconstruction
    """
    await websocket.accept()
    logger.info("Quest client connected")

    session_id = None
    handler = None

    try:
        while True:
            data = await websocket.receive()

            if "text" in data:
                msg = json.loads(data["text"])
                action = msg.get("action")

                if action == "start_session":
                    session_name = msg.get("name", "scan")
                    session_id = (
                        f"{session_name}_"
                        f"{time.strftime('%Y%m%d_%H%M%S')}_"
                        f"{uuid.uuid4().hex[:6]}"
                    )
                    handler = FrameHandler(session_id)
                    active_sessions[session_id] = {
                        "handler": handler,
                        "created_at": time.time(),
                        "status": "capturing",
                    }
                    await websocket.send_json({
                        "action": "session_started",
                        "session_id": session_id,
                    })
                    logger.info(f"Session started: {session_id}")

                elif action == "stop_session":
                    if handler:
                        handler.save_session_metadata()
                        handler.prepare_colmap_input()
                        frame_count = handler.accepted_frames

                        active_sessions[session_id]["status"] = "captured"
                        await websocket.send_json({
                            "action": "session_stopped",
                            "session_id": session_id,
                            "total_frames": frame_count,
                            "ready_for_reconstruction": (
                                frame_count >= MIN_FRAMES_FOR_RECONSTRUCTION
                            ),
                        })
                        logger.info(
                            f"Session stopped: {session_id} ({frame_count} frames)"
                        )
                    else:
                        await websocket.send_json({
                            "action": "error",
                            "message": "No active session",
                        })

                elif action == "start_reconstruction":
                    method = msg.get("method", "full")
                    if session_id and handler:
                        await websocket.send_json({
                            "action": "reconstruction_started",
                            "session_id": session_id,
                            "method": method,
                        })

                elif action == "ping":
                    await websocket.send_json({"action": "pong"})

            elif "bytes" in data:
                if handler is None:
                    await websocket.send_json({
                        "action": "error",
                        "message": "No active session. Send start_session first.",
                    })
                    continue

                result = handler.process_frame(data["bytes"])
                await websocket.send_json({
                    "action": "frame_result",
                    **result,
                })

    except WebSocketDisconnect:
        logger.info(f"Quest client disconnected (session: {session_id})")
        if handler:
            handler.save_session_metadata()
            handler.prepare_colmap_input()
    except Exception as e:
        logger.error(f"WebSocket error: {e}", exc_info=True)
        if handler:
            handler.save_session_metadata()


# =============================================================================
# Entry Point
# =============================================================================

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(
        "main:app",
        host=SERVER_HOST,
        port=SERVER_PORT,
        reload=False,
        log_level="info",
    )
