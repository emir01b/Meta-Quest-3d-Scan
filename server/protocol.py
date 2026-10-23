"""Versioned, bounded wire format for immutable scan frames.

MSF2: magic (4), metadata length, JPEG length, depth length (uint32 LE),
then UTF-8 JSON, JPEG and optional depth bytes. Depth is accepted only with an
explicit calibrated registration contract; the Quest client currently sends
RGB only because screen-space Environment Depth is not a camera depth map.
"""

import json
import math
import struct

MAGIC = b"MSF2"
HEADER = struct.Struct("<4sIII")
MAX_METADATA = 16 * 1024
MAX_IMAGE = 16 * 1024 * 1024
MAX_DEPTH = 32 * 1024 * 1024
CAMERAS = {"left", "right"}


class ProtocolError(ValueError):
    pass


def _finite(value, label):
    if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value):
        raise ProtocolError(f"{label} must be finite")
    return float(value)


def parse_frame(data: bytes) -> tuple[dict, bytes, bytes]:
    if len(data) < HEADER.size:
        raise ProtocolError("short header")
    magic, meta_len, image_len, depth_len = HEADER.unpack_from(data)
    if magic != MAGIC:
        raise ProtocolError("unsupported frame version; MSF2 required")
    if not (2 <= meta_len <= MAX_METADATA and 32 <= image_len <= MAX_IMAGE and depth_len <= MAX_DEPTH):
        raise ProtocolError("invalid frame lengths")
    if len(data) != HEADER.size + meta_len + image_len + depth_len:
        raise ProtocolError("frame length mismatch")
    offset = HEADER.size
    try:
        meta = json.loads(data[offset:offset + meta_len].decode("utf-8"))
    except (UnicodeError, ValueError) as exc:
        raise ProtocolError("invalid metadata JSON") from exc
    offset += meta_len
    image = data[offset:offset + image_len]
    depth = data[offset + image_len:]
    if not image.startswith(b"\xff\xd8") or not image.endswith(b"\xff\xd9"):
        raise ProtocolError("image is not a complete JPEG")
    if not isinstance(meta, dict) or meta.get("protocol_version") != 2:
        raise ProtocolError("invalid metadata version")
    if type(meta.get("frame_index")) is not int or not 0 <= meta["frame_index"] < 2**32:
        raise ProtocolError("invalid frame index")
    if meta.get("camera_id") not in CAMERAS:
        raise ProtocolError("physical camera id required")
    width, height = meta.get("image_width"), meta.get("image_height")
    if type(width) is not int or type(height) is not int or not (16 <= width <= 4096 and 16 <= height <= 4096):
        raise ProtocolError("invalid image dimensions")
    if meta.get("image_orientation") != "top_left":
        raise ProtocolError("unsupported image orientation")
    if meta.get("pose_space") != "unity_world_camera_to_world":
        raise ProtocolError("physical camera pose required")
    intr = meta.get("intrinsics")
    pose = meta.get("pose")
    if not isinstance(intr, dict) or not isinstance(pose, dict):
        raise ProtocolError("intrinsics and pose required")
    for key in ("fx", "fy", "cx", "cy"):
        _finite(intr.get(key), f"intrinsics.{key}")
    if intr["fx"] <= 0 or intr["fy"] <= 0 or not (0 <= intr["cx"] <= width and 0 <= intr["cy"] <= height):
        raise ProtocolError("invalid intrinsics")
    position, rotation = pose.get("position"), pose.get("rotation")
    if not isinstance(position, dict) or not isinstance(rotation, dict):
        raise ProtocolError("invalid pose")
    for key in ("x", "y", "z"):
        _finite(position.get(key), f"position.{key}")
        if abs(position[key]) > 1000:
            raise ProtocolError("camera position outside supported tracking range")
    for key in ("x", "y", "z", "w"):
        _finite(rotation.get(key), f"rotation.{key}")
    norm = sum(rotation[key] ** 2 for key in ("x", "y", "z", "w"))
    if not 0.98 <= norm <= 1.02:
        raise ProtocolError("camera quaternion is not normalized")
    timestamp = meta.get("camera_timestamp")
    if not isinstance(timestamp, str) or not timestamp or len(timestamp) > 64:
        raise ProtocolError("camera timestamp required")
    if depth:
        # A screen-space depth texture lacks the calibration needed for metric
        # RGB-D fusion. Refuse it until an explicit registered-depth contract exists.
        raise ProtocolError("registered metric depth protocol is not implemented")
    return meta, image, depth
