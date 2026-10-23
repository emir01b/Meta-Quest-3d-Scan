"""Validate and durably store physical-camera frames without altering source data."""

import json
import hashlib
import logging
import math
import os
import re
import shutil
import time
from pathlib import Path

import cv2
import numpy as np
from scipy.spatial.transform import Rotation

from config import BLUR_THRESHOLD, EXPOSURE_MIN, EXPOSURE_MAX, SESSIONS_DIR
from protocol import ProtocolError, parse_frame

logger = logging.getLogger("metascan.frame_handler")


def atomic_write(path: Path, data: bytes):
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("wb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


class FrameHandler:
    def __init__(self, session_id: str, root_dir: Path = SESSIONS_DIR, roi=None):
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,96}", session_id):
            raise ValueError("invalid session id")
        self.session_id = session_id
        self.session_dir = root_dir / session_id
        self.images_dir = self.session_dir / "images"
        self.depth_dir = self.session_dir / "depth"
        self.poses_dir = self.session_dir / "poses"
        for path in (self.images_dir, self.depth_dir, self.poses_dir):
            path.mkdir(parents=True, exist_ok=True)
        self.start_time = time.time()
        self.frame_count = 0
        self.accepted_frames = 0
        self.rejected_frames = 0
        self.frame_metadata = []
        self.intrinsics = None
        self.camera_id = None
        self.roi = roi
        self.finalized = False
        self.seen_indices = set()
        self.seen_timestamps = set()

    def process_frame(self, raw_data: bytes) -> dict:
        if self.finalized:
            return self._reject("session_finalized")
        self.frame_count += 1
        try:
            meta, image_bytes, _ = parse_frame(raw_data)
            index = meta["frame_index"]
            if index in self.seen_indices or meta["camera_timestamp"] in self.seen_timestamps:
                return self._reject("duplicate_frame")
            image = cv2.imdecode(np.frombuffer(image_bytes, np.uint8), cv2.IMREAD_COLOR)
            if image is None or (image.shape[1], image.shape[0]) != (meta["image_width"], meta["image_height"]):
                return self._reject("image_decode_or_dimensions")
            intr = {**meta["intrinsics"], "width": image.shape[1], "height": image.shape[0], "model": "PINHOLE"}
            if self.intrinsics is not None:
                if meta["camera_id"] != self.camera_id or any(
                    not math.isclose(intr[key], self.intrinsics[key], rel_tol=1e-5, abs_tol=1e-3)
                    for key in ("fx", "fy", "cx", "cy", "width", "height")
                ):
                    return self._reject("camera_calibration_changed")
            quality_ok, quality_issues, metrics = self._check_quality(image)
            filename = f"frame_{index:06d}.jpg"
            atomic_write(self.images_dir / filename, image_bytes)
            if self.intrinsics is None:
                self.intrinsics = intr
                self.camera_id = meta["camera_id"]
                atomic_write(self.session_dir / "intrinsics.json", json.dumps(intr, indent=2).encode("utf-8"))
            frame = {
                "index": self.accepted_frames,
                "original_index": index,
                "camera_timestamp": meta["camera_timestamp"],
                "camera_id": meta["camera_id"],
                "image": filename,
                "image_sha256": hashlib.sha256(image_bytes).hexdigest(),
                "depth": None,
                "pose": meta["pose"],
                "quality_ok": quality_ok,
                "quality_issues": quality_issues,
                "quality_metrics": metrics,
                "capture_metadata": meta,
            }
            self.frame_metadata.append(frame)
            self.accepted_frames += 1
            self.seen_indices.add(index)
            self.seen_timestamps.add(meta["camera_timestamp"])
            return {
                "status": "accepted", "frame_index": index,
                "total_frames": self.accepted_frames,
                "quality_ok": quality_ok, "quality_issues": quality_issues,
            }
        except (ProtocolError, ValueError, cv2.error) as exc:
            return self._reject(str(exc))
        except OSError:
            logger.exception("Frame storage failed")
            return self._reject("storage_failed")

    def _reject(self, reason):
        self.rejected_frames += 1
        logger.warning("Rejected scan frame: %s", reason)
        return {"status": "rejected", "reason": reason, "total_frames": self.accepted_frames,
                "quality_ok": False, "quality_issues": [reason]}

    @staticmethod
    def _check_quality(image):
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        sharpness = float(cv2.Laplacian(gray, cv2.CV_64F).var())
        brightness = float(gray.mean())
        issues = []
        if sharpness < BLUR_THRESHOLD:
            issues.append("blurry")
        if brightness < EXPOSURE_MIN:
            issues.append("underexposed")
        elif brightness > EXPOSURE_MAX:
            issues.append("overexposed")
        return not issues, issues, {"laplacian_variance": sharpness, "mean_brightness": brightness}

    @staticmethod
    def _unity_pose_to_colmap(pose):
        pos = pose["position"]
        rot = pose["rotation"]
        q = np.array([-rot["x"], rot["y"], -rot["z"], rot["w"]])
        # First convert both Unity world/camera with Y reflection, then
        # rotate the RH world 180 degrees around X so physical Y stays up.
        upright = np.diag([1.0, -1.0, -1.0])
        world_camera = upright @ Rotation.from_quat(q).as_matrix()
        world_position = np.array([pos["x"], pos["y"], -pos["z"]])
        camera_world = world_camera.T
        q_camera_world = Rotation.from_matrix(camera_world).as_quat()
        t = -camera_world @ world_position
        return q_camera_world[3], *q_camera_world[:3], *t

    def save_session_metadata(self, status="captured"):
        elapsed = time.time() - self.start_time
        metadata = {
            "protocol_version": 2, "session_id": self.session_id,
            "status": status, "total_frames": self.accepted_frames,
            "received_frames": self.frame_count, "rejected_frames": self.rejected_frames,
            "duration_seconds": round(elapsed, 2),
            "timestamp": self.start_time, "camera_id": self.camera_id,
            "intrinsics": self.intrinsics, "roi": self.roi,
            "frames": self.frame_metadata,
        }
        atomic_write(self.session_dir / "session_metadata.json", json.dumps(metadata, indent=2).encode("utf-8"))
        self.finalized = True
        return metadata

    def prepare_colmap_input(self):
        """Keep a reproducible original-JPEG input; never change captured pixels."""
        colmap_dir = self.session_dir / "colmap"
        images_dir = colmap_dir / "images"
        images_dir.mkdir(parents=True, exist_ok=True)
        for frame in self.frame_metadata:
            src = self.images_dir / frame["image"]
            dst = images_dir / frame["image"]
            if src.exists() and not dst.exists():
                shutil.copy2(src, dst)
        # Reference poses are not a COLMAP registration. Alignment must
        # explicitly consume and verify them before claiming metric scale.
        if self.intrinsics:
            intr = self.intrinsics
            camera_text = ("# CAMERA_ID, MODEL, WIDTH, HEIGHT, PARAMS[]\n"
                           f"1 PINHOLE {intr['width']} {intr['height']} "
                           f"{intr['fx']} {intr['fy']} {intr['cx']} {intr['cy']}\n")
            atomic_write(colmap_dir / "cameras.txt", camera_text.encode("utf-8"))
        lines = ["# IMAGE_ID, QW, QX, QY, QZ, TX, TY, TZ, CAMERA_ID, NAME\n"]
        for i, frame in enumerate(self.frame_metadata, 1):
            pose_values = self._unity_pose_to_colmap(frame["pose"])
            lines.append(f"{i} {' '.join(map(str, pose_values))} 1 {frame['image']}\n\n")
        atomic_write(colmap_dir / "images.txt", "".join(lines).encode("utf-8"))
        atomic_write(colmap_dir / "points3D.txt", b"# Empty reference model\n")
