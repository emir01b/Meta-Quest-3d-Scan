"""
MetaScan — Main Server
FastAPI + WebSocket server that receives data from Quest 3S
and orchestrates 3D reconstruction.
"""
import asyncio
import json
import logging
import math
import re
import time
import uuid
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, WebSocket, WebSocketDisconnect, HTTPException
from fastapi.staticfiles import StaticFiles
from fastapi.responses import JSONResponse, FileResponse
from fastapi.middleware.cors import CORSMiddleware

from config import (
    SERVER_HOST, SERVER_PORT, SESSIONS_DIR, EXPORTS_DIR,
    MIN_FRAMES_FOR_RECONSTRUCTION
)
from frame_handler import FrameHandler, atomic_write
from reconstruction import ReconstructionPipeline

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(name)s] %(levelname)s: %(message)s",
    datefmt="%H:%M:%S",
)
logger = logging.getLogger("metascan.server")

reconstruction_tasks: dict[str, asyncio.Task] = {}
reconstruction_progress: dict[str, dict] = {}


def _session_dir(session_id: str) -> Path:
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,96}", session_id):
        raise HTTPException(status_code=400, detail="Invalid session id")
    return SESSIONS_DIR / session_id


def _validate_roi(roi):
    if roi is None or roi == {}:
        return None
    try:
        center = roi["center"]
        numbers = [float(center[k]) for k in ("x", "y", "z")]
        radius = float(roi["radius"])
        if not all(math.isfinite(v) for v in numbers + [radius]) or not 0.01 <= radius <= 5:
            raise ValueError("invalid ROI")
        return {"center": dict(zip(("x", "y", "z"), numbers)), "radius": radius}
    except (TypeError, KeyError, ValueError) as exc:
        raise ValueError("Invalid object selection volume") from exc


def _job_status(session_id: str) -> dict:
    task = reconstruction_tasks.get(session_id)
    if task is None:
        if session_id is None:
            return {"status": "not_started"}
        state_file = _session_dir(session_id) / "reconstruction_job.json"
        if state_file.exists():
            state = json.loads(state_file.read_text(encoding="utf-8"))
            if state.get("status") == "running":
                return {"status": "interrupted", "step": "Server stopped during reconstruction"}
            return state
        return {"status": "not_started"}
    progress = reconstruction_progress.get(session_id, {"progress": 0, "step": ""})
    if not task.done():
        return {"status": "running", **progress}
    try:
        result = task.result()
    except Exception as exc:
        return {"status": "failed", "error": str(exc), **progress}
    return {"status": result.get("status", "failed"), "result": result,
            "progress": 100 if result.get("status") == "completed" else progress["progress"],
            "step": "Done" if result.get("status") == "completed" else result.get("error", "Failed")}


def _start_job(session_id: str, method: str, quality: str, output_dir: str = None) -> dict:
    session_dir = _session_dir(session_id)
    meta_path = session_dir / "session_metadata.json"
    if not meta_path.exists():
        raise HTTPException(status_code=404, detail="Finalized session not found")
    metadata = json.loads(meta_path.read_text(encoding="utf-8"))
    if metadata.get("status") != "captured" or metadata.get("protocol_version") != 2:
        raise HTTPException(status_code=409, detail="Session is not a finalized physical-camera capture")
    if metadata.get("total_frames", 0) < MIN_FRAMES_FOR_RECONSTRUCTION:
        raise HTTPException(status_code=409, detail="Not enough captured frames")
    if method not in ("colmap", "tsdf") or quality not in ("low", "medium", "high"):
        raise HTTPException(status_code=400, detail="Invalid reconstruction method or quality")
    if method == "tsdf":
        raise HTTPException(status_code=400, detail="Registered metric depth is not available")
    if output_dir is not None and not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", output_dir):
        raise HTTPException(status_code=400, detail="Output subfolder must be a simple name")
    existing = reconstruction_tasks.get(session_id)
    if existing is not None and not existing.done():
        return {"status": "already_running", "session_id": session_id}
    reconstruction_progress[session_id] = {"progress": 0, "step": "Starting"}
    state_file = session_dir / "reconstruction_job.json"
    atomic_write(state_file, json.dumps({"status": "running", "method": method,
                                         "quality": quality}).encode("utf-8"))
    loop = asyncio.get_running_loop()

    def progress_callback(progress: int, step: str):
        loop.call_soon_threadsafe(reconstruction_progress.__setitem__, session_id,
                                  {"progress": progress, "step": step})

    def execute():
        output = EXPORTS_DIR / session_id / output_dir if output_dir else None
        pipeline = ReconstructionPipeline(session_dir, session_id, output)
        result = pipeline.run_colmap_pipeline(quality, progress_callback)
        atomic_write(state_file, json.dumps({
            "status": result.get("status", "failed"),
            "result": result, "progress": 100 if result.get("status") == "completed" else 0,
            "step": "Done" if result.get("status") == "completed" else result.get("error", "Failed"),
        }, default=str).encode("utf-8"))
        return result

    reconstruction_tasks[session_id] = asyncio.create_task(asyncio.to_thread(execute))
    return {"status": "started", "session_id": session_id, "method": method, "quality": quality}


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
    session_dir = _session_dir(session_id)
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
async def start_reconstruction(session_id: str, method: str = "colmap", quality: str = "medium", output_dir: str = None):
    """
    Start 3D reconstruction for a session.
    method: 'colmap'; 'tsdf' is rejected until calibrated metric depth exists
    quality: 'low', 'medium', 'high'
    output_dir: Optional simple subfolder under the session's export directory
    """
    return _start_job(session_id, method, quality, output_dir)


@app.get("/api/sessions/{session_id}/reconstruction/status")
async def reconstruction_status(session_id: str):
    """Check reconstruction status."""
    _session_dir(session_id)
    return _job_status(session_id)


@app.get("/api/sessions/{session_id}/exports")
async def list_exports(session_id: str):
    """List exported files for a session."""
    _session_dir(session_id)
    state = _job_status(session_id)
    result = state.get("result", {})
    return {"exports": result.get("exports", []) if state.get("status") == "completed" else [],
            "status": state.get("status", "not_started")}


@app.get("/api/sessions/{session_id}/exports/{filename}")
async def download_export(session_id: str, filename: str):
    _session_dir(session_id)
    state = _job_status(session_id)
    if state.get("status") != "completed":
        raise HTTPException(status_code=404, detail="No completed export")
    for item in state.get("result", {}).get("exports", []):
        if item.get("filename") == filename:
            path = Path(item["path"])
            if path.name != filename or not path.is_file():
                break
            return FileResponse(path, filename=filename)
    raise HTTPException(status_code=404, detail="Export not found")


# =============================================================================
# WebSocket — Real-time Frame Streaming
# =============================================================================

@app.websocket("/ws/scan")
async def websocket_scan(websocket: WebSocket):
    """Ordered MSF2 ingest. Stop ACK follows durable frame and metadata writes."""
    await websocket.accept()
    logger.info("Quest client connected")
    session_id = None
    handler = None
    try:
        while True:
            data = await websocket.receive()
            if data.get("type") == "websocket.disconnect":
                break
            if data.get("text") is not None:
                try:
                    msg = json.loads(data["text"])
                except ValueError:
                    await websocket.send_json({"action": "error", "message": "Invalid JSON command"})
                    continue
                if not isinstance(msg, dict):
                    await websocket.send_json({"action": "error", "message": "Invalid command"})
                    continue
                action = msg.get("action")
                if action == "start_session":
                    if handler is not None:
                        await websocket.send_json({"action": "error", "message": "Session already started"})
                        continue
                    try:
                        roi = _validate_roi(msg.get("roi"))
                    except ValueError as exc:
                        await websocket.send_json({"action": "error", "message": str(exc)})
                        continue
                    raw_name = str(msg.get("name", "scan"))[:32]
                    session_name = re.sub(r"[^A-Za-z0-9_-]", "_", raw_name) or "scan"
                    session_id = (
                        f"{session_name}_"
                        f"{time.strftime('%Y%m%d_%H%M%S')}_"
                        f"{uuid.uuid4().hex[:6]}"
                    )
                    handler = FrameHandler(session_id, roi=roi)
                    await websocket.send_json({"action": "session_started", "session_id": session_id})
                elif action == "stop_session":
                    if handler:
                        if not handler.finalized:
                            await asyncio.to_thread(handler.prepare_colmap_input)
                            await asyncio.to_thread(handler.save_session_metadata, "captured")
                        frame_count = handler.accepted_frames
                        await websocket.send_json({
                            "action": "session_stopped", "session_id": session_id,
                            "total_frames": frame_count,
                            "ready_for_reconstruction": frame_count >= MIN_FRAMES_FOR_RECONSTRUCTION,
                        })
                        # Permit another scan on the same connection. Retain
                        # session_id so reconstruction/status still address the
                        # session just completed until the next start_session.
                        handler = None
                    else:
                        await websocket.send_json({"action": "error", "message": "No active session"})
                elif action == "start_reconstruction":
                    try:
                        if session_id is None:
                            raise HTTPException(status_code=409, detail="No session")
                        outcome = _start_job(session_id, msg.get("method", "colmap"),
                                             msg.get("quality", "medium"))
                        await websocket.send_json({"action": "reconstruction_started", **outcome})
                    except HTTPException as exc:
                        await websocket.send_json({"action": "error", "message": str(exc.detail)})
                elif action == "get_reconstruction_status":
                    await websocket.send_json({
                        "action": "reconstruction_status", **_job_status(session_id)
                    })
                elif action == "ping":
                    await websocket.send_json({"action": "pong"})
                else:
                    await websocket.send_json({"action": "error", "message": "Unknown action"})
            elif data.get("bytes") is not None:
                if handler is None or handler.finalized:
                    await websocket.send_json({
                        "action": "error", "message": "No capturing session",
                    })
                    continue
                result = await asyncio.to_thread(handler.process_frame, data["bytes"])
                await websocket.send_json({"action": "frame_result", **result})
    except WebSocketDisconnect:
        pass
    except Exception:
        logger.exception("WebSocket scan failed for %s", session_id)
    finally:
        if handler is not None and not handler.finalized:
            # A missing stop ACK is never represented as a complete capture.
            handler.save_session_metadata("interrupted")
        logger.info("Quest client disconnected (session: %s)", session_id)


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
        ws="websockets",
        log_level="info",
    )
