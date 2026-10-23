"""Metric object reconstruction from immutable Quest RGB captures.

COLMAP reconstructs image geometry, then aligns it to the physical camera
centres recorded by PCA. No export is labelled metric if alignment fails.
"""

import hashlib
import json
import logging
import math
import shutil
import subprocess
import time
from pathlib import Path

import cv2
import numpy as np
import trimesh
from scipy.spatial.transform import Rotation

from config import (
    COLMAP_BINARY, EXPORTS_DIR, MIN_FRAMES_FOR_RECONSTRUCTION,
    MAX_FRAMES_FOR_RECONSTRUCTION,
)
from frame_handler import atomic_write

logger = logging.getLogger("metascan.reconstruction")
PIPELINE_VERSION = "metric-rgb-v2"
STAGES = ("features", "matching", "mapper", "aligned", "alignment_text",
          "undistort", "stereo", "fusion", "meshing")


class ReconstructionError(RuntimeError):
    pass


def make_match_pairs(frames: list[dict], neighbors: int = 12, loops: int = 5) -> list[tuple[str, str]]:
    """Temporal overlap plus spatial loop closures from physical camera centres."""
    pairs = set()
    positions = np.array([
        [frame["pose"]["position"][axis] for axis in ("x", "y", "z")]
        for frame in frames
    ], dtype=float)
    for i, frame in enumerate(frames):
        for j in range(i + 1, min(len(frames), i + neighbors + 1)):
            pairs.add(tuple(sorted((frame["image"], frames[j]["image"]))))
        if loops and len(frames) > neighbors * 2:
            distances = np.linalg.norm(positions - positions[i], axis=1)
            candidates = [
                j for j in np.argsort(distances)
                if abs(j - i) > neighbors and 0.02 <= distances[j] <= 0.20
            ]
            for j in candidates[:loops]:
                pairs.add(tuple(sorted((frame["image"], frames[j]["image"]))))
    return sorted(pairs)


def _safe_reset(path: Path, root: Path):
    resolved, allowed = path.resolve(), root.resolve()
    if resolved == allowed or allowed not in resolved.parents or path.is_symlink():
        raise ReconstructionError("unsafe generated path")
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True)


def make_roi_masks(metadata: dict, mask_dir: Path):
    """Project the selected Unity-world sphere into each physical camera image.

    These masks constrain dense fusion, leaving background features available
    for SfM. The mask is deliberately conservative at the sphere boundary.
    """
    roi = metadata.get("roi")
    if not roi:
        return None
    mask_dir.mkdir(parents=True, exist_ok=True)
    center = np.array([roi["center"][k] for k in ("x", "y", "z")], dtype=float)
    radius = float(roi["radius"]) * 1.15
    intr = metadata["intrinsics"]
    width, height = int(intr["width"]), int(intr["height"])
    for frame in metadata["frames"]:
        pose = frame["pose"]
        location = np.array([pose["position"][k] for k in ("x", "y", "z")], dtype=float)
        quaternion = [pose["rotation"][k] for k in ("x", "y", "z", "w")]
        local = Rotation.from_quat(quaternion).inv().apply(center - location)
        mask = np.zeros((height, width), dtype=np.uint8)
        if local[2] <= 0:
            pass  # Object is behind the physical camera.
        elif local[2] <= radius:
            # Degenerate camera-inside-ROI case: do not silently discard all
            # potentially useful pixels.
            mask[:] = 255
        else:
            u = intr["fx"] * local[0] / local[2] + intr["cx"]
            v = intr["cy"] - intr["fy"] * local[1] / local[2]
            denominator = local[2] - radius
            rx = min(width * 2, int(math.ceil(intr["fx"] * radius / denominator)))
            ry = min(height * 2, int(math.ceil(intr["fy"] * radius / denominator)))
            cv2.ellipse(mask, (int(round(u)), int(round(v))),
                        (max(1, rx), max(1, ry)), 0, 0, 360, 255, -1)
        destination = mask_dir / (frame["image"] + ".png")
        if not cv2.imwrite(str(destination), mask):
            raise ReconstructionError(f"Failed to write ROI mask: {destination}")
    return mask_dir


def evaluate_metric_alignment(images_txt: Path, frames: list[dict]) -> dict:
    """Check all registered COLMAP centres against independent Quest poses."""
    reference = {
        frame["image"]: np.array([frame["pose"]["position"][axis] * sign
                                  for axis, sign in (("x", 1), ("y", 1), ("z", -1))], dtype=float)
        for frame in frames
    }
    errors = []
    for line in images_txt.read_text(encoding="utf-8").splitlines():
        fields = line.split()
        if len(fields) < 10 or not fields[0].isdigit() or fields[9] not in reference:
            continue
        q = np.array([float(v) for v in fields[1:5]])
        t = np.array([float(v) for v in fields[5:8]])
        if not np.isfinite(q).all() or not np.isfinite(t).all():
            raise ReconstructionError("Non-finite aligned camera pose")
        rotation = Rotation.from_quat([q[1], q[2], q[3], q[0]])
        centre = -rotation.inv().apply(t)
        errors.append(float(np.linalg.norm(centre - reference[fields[9]])))
    required = max(20, math.ceil(len(frames) * 0.5))
    if len(errors) < required:
        raise ReconstructionError(f"Only {len(errors)}/{len(frames)} images registered; need {required}")
    result = {
        "registered_images": len(errors), "input_images": len(frames),
        "registered_fraction": len(errors) / len(frames),
        "median_camera_error_m": float(np.median(errors)),
        "p90_camera_error_m": float(np.percentile(errors, 90)),
        "within_5cm_fraction": float(np.mean(np.asarray(errors) <= 0.05)),
    }
    if result["median_camera_error_m"] > 0.03 or result["within_5cm_fraction"] < 0.8:
        raise ReconstructionError(f"Physical camera alignment is inconsistent: {result}")
    return result


class ReconstructionPipeline:
    def __init__(self, session_dir: Path, session_id: str, custom_output_dir: Path = None):
        self.session_dir = Path(session_dir)
        self.session_id = session_id
        self.images_dir = self.session_dir / "colmap" / "images"
        self.colmap_dir = self.session_dir / "colmap"
        self.output_dir = Path(custom_output_dir) if custom_output_dir else EXPORTS_DIR / session_id
        self.quality = "medium"
        self.progress_cb = None
        self.timings = {}
        self.logs = {}

    def _progress(self, value, step):
        if self.progress_cb:
            self.progress_cb(value, step)

    def _step(self, name, args, timeout, checkpoint=None, required=()):
        if checkpoint is not None and checkpoint.exists() and all(path.exists() for path in required):
            self.logs[name] = "cached"
            self.timings[name] = 0.0
            return
        if checkpoint is not None:
            checkpoint.unlink(missing_ok=True)
            for later in STAGES[STAGES.index(name) + 1:]:
                (self.run_dir / f"{later}.ok").unlink(missing_ok=True)
        started = time.perf_counter()
        logger.info("COLMAP %s", name)
        try:
            result = subprocess.run(args, capture_output=True, text=True,
                                    encoding="utf-8", errors="replace", timeout=timeout)
        except subprocess.TimeoutExpired as exc:
            raise ReconstructionError(f"{name} timed out after {timeout}s") from exc
        elapsed = round(time.perf_counter() - started, 2)
        self.timings[name] = elapsed
        output = (result.stdout or "") + "\n" + (result.stderr or "")
        log_path = self.run_dir / f"{name}.log"
        atomic_write(log_path, output.encode("utf-8"))
        self.logs[name] = str(log_path)
        if result.returncode:
            raise ReconstructionError(f"{name} failed (exit {result.returncode}): {output[-600:]}")
        if any(not path.exists() for path in required):
            raise ReconstructionError(f"{name} finished without required output")
        if checkpoint is not None:
            atomic_write(checkpoint, b"ok\n")

    def _load_session(self):
        path = self.session_dir / "session_metadata.json"
        if not path.exists():
            raise ReconstructionError("captured session metadata is missing")
        raw = path.read_bytes()
        metadata = json.loads(raw)
        if metadata.get("protocol_version") != 2 or metadata.get("status") != "captured":
            raise ReconstructionError("Only finalized MSF2 physical-camera sessions can produce metric exports")
        frames = metadata.get("frames", [])
        if not MIN_FRAMES_FOR_RECONSTRUCTION <= len(frames) <= MAX_FRAMES_FOR_RECONSTRUCTION:
            raise ReconstructionError(f"Expected {MIN_FRAMES_FOR_RECONSTRUCTION}–{MAX_FRAMES_FOR_RECONSTRUCTION} frames; got {len(frames)}")
        if not metadata.get("intrinsics") or len({frame["camera_id"] for frame in frames}) != 1:
            raise ReconstructionError("Fixed physical camera calibration is required")
        if any(not (self.images_dir / frame["image"]).is_file() for frame in frames):
            raise ReconstructionError("COLMAP input is incomplete")
        for frame in frames:
            expected = frame.get("image_sha256")
            if not expected or len(expected) != 64:
                raise ReconstructionError("Captured image checksum is missing")
            for image_path in (self.session_dir / "images" / frame["image"],
                               self.images_dir / frame["image"]):
                if not image_path.is_file() or hashlib.sha256(image_path.read_bytes()).hexdigest() != expected:
                    raise ReconstructionError(f"Captured image changed or is missing: {frame['image']}")
        return metadata, raw

    def run_colmap_pipeline(self, quality="medium", progress_cb=None):
        self.quality, self.progress_cb = quality, progress_cb
        result = {"session_id": self.session_id, "method": "colmap",
                  "status": "failed", "exports": [], "timings_seconds": self.timings}
        try:
            if quality not in ("low", "medium", "high"):
                raise ReconstructionError("Unsupported quality preset")
            binary = Path(COLMAP_BINARY)
            if not binary.is_file():
                raise ReconstructionError(f"COLMAP binary missing: {binary}")
            metadata, raw_metadata = self._load_session()
            identity = hashlib.sha256(raw_metadata + PIPELINE_VERSION.encode()
                                      + quality.encode() + str(binary.stat().st_mtime_ns).encode()).hexdigest()[:16]
            self.run_dir = self.colmap_dir / "runs" / identity
            self.run_dir.mkdir(parents=True, exist_ok=True)
            self.output_dir.mkdir(parents=True, exist_ok=True)
            result["run_id"] = identity
            result["alignment_validation"] = self._run_colmap(str(binary), metadata)
            result["alignment"] = "pca_camera_centres_metric"
            result["exports"], result["mesh_validation"] = self._export_metric_mesh(metadata)
            if not result["exports"]:
                raise ReconstructionError("No validated mesh exports were produced")
            result["status"] = "completed"
            self._progress(100, "Completed")
        except (ReconstructionError, OSError, ValueError, KeyError) as exc:
            result["error"] = str(exc)
            logger.exception("Reconstruction failed for %s", self.session_id)
            self._progress(0, "Failed: " + str(exc)[:100])
        result["timings_seconds"] = self.timings
        result["logs"] = self.logs
        if self.output_dir.exists():
            atomic_write(self.output_dir / "reconstruction_result.json",
                         json.dumps(result, indent=2, default=str).encode("utf-8"))
        return result

    def _run_colmap(self, binary, metadata):
        frames = metadata["frames"]
        intr = metadata["intrinsics"]
        db = self.run_dir / "database.db"
        sparse = self.run_dir / "sparse"
        aligned = self.run_dir / "aligned"
        dense = self.run_dir / "dense"
        sparse.mkdir(exist_ok=True)
        aligned.mkdir(exist_ok=True)
        dense.mkdir(exist_ok=True)
        max_features = {"low": 4096, "medium": 8192, "high": 16384}[self.quality]
        image_size = max(intr["width"], intr["height"])
        work_size = min(image_size, 960) if self.quality == "low" else image_size
        stereo_iterations = {"low": 3, "medium": 5, "high": 7}[self.quality]
        stereo_samples = {"low": 10, "medium": 15, "high": 20}[self.quality]
        poisson_depth = {"low": 9, "medium": 10, "high": 11}[self.quality]
        params = ",".join(str(intr[key]) for key in ("fx", "fy", "cx", "cy"))
        self._progress(10, "GPU SIFT feature extraction")
        self._step("features", [
            binary, "feature_extractor", "--database_path", str(db),
            "--image_path", str(self.images_dir), "--ImageReader.single_camera", "1",
            "--ImageReader.camera_model", "PINHOLE", "--ImageReader.camera_params", params,
            "--FeatureExtraction.use_gpu", "1",
            "--FeatureExtraction.max_image_size", str(work_size),
            "--SiftExtraction.max_num_features", str(max_features),
            "--SiftExtraction.domain_size_pooling", "0",
        ], 1200, self.run_dir / "features.ok", (db,))

        pairs = make_match_pairs(frames)
        if len(pairs) < len(frames) - 1:
            raise ReconstructionError("Insufficient candidate image pairs")
        pair_path = self.run_dir / "pairs.txt"
        atomic_write(pair_path, "".join(f"{a} {b}\n" for a, b in pairs).encode())
        self._progress(22, f"GPU matching ({len(pairs)} candidate pairs)")
        self._step("matching", [
            binary, "matches_importer", "--database_path", str(db),
            "--match_list_path", str(pair_path), "--match_type", "pairs",
            "--FeatureMatching.use_gpu", "1",
        ], 1800, self.run_dir / "matching.ok", (db,))

        self._progress(38, "Sparse reconstruction")
        mapper_ok = self.run_dir / "mapper.ok"
        model = sparse / "0"
        if not mapper_ok.exists() or not (model / "points3D.bin").is_file():
            _safe_reset(sparse, self.run_dir)
        self._step("mapper", [
            binary, "mapper", "--database_path", str(db), "--image_path", str(self.images_dir),
            "--output_path", str(sparse), "--Mapper.ba_use_gpu", "0",
            "--Mapper.ba_refine_focal_length", "0",
            "--Mapper.ba_refine_principal_point", "0",
            "--Mapper.ba_refine_extra_params", "0",
        ], 2400, mapper_ok, (model / "points3D.bin",))
        if not (model / "points3D.bin").is_file():
            raise ReconstructionError("COLMAP did not register a sparse model")

        # COLMAP scale is arbitrary until its camera centres are aligned to
        # PCA physical camera positions. Flip world Z to keep Y up in a RH world.
        ref_path = self.run_dir / "camera_centres.txt"
        ref_lines = []
        for frame in frames:
            p = frame["pose"]["position"]
            ref_lines.append(f"{frame['image']} {p['x']} {p['y']} {-p['z']}\n")
        atomic_write(ref_path, "".join(ref_lines).encode())
        self._progress(48, "Metric camera alignment")
        align_ok = self.run_dir / "aligned.ok"
        if not align_ok.exists() or not (aligned / "points3D.bin").is_file():
            _safe_reset(aligned, self.run_dir)
        self._step("aligned", [
            binary, "model_aligner", "--input_path", str(model), "--output_path", str(aligned),
            "--ref_images_path", str(ref_path), "--ref_is_gps", "0",
            "--alignment_type", "custom", "--alignment_max_error", "0.05",
            "--min_common_images", "5",
        ], 300, align_ok, (aligned / "points3D.bin",))
        if not (aligned / "points3D.bin").is_file():
            raise ReconstructionError("Metric alignment produced no model")
        aligned_text = self.run_dir / "aligned_text"
        aligned_text.mkdir(exist_ok=True)
        self._step("alignment_text", [
            binary, "model_converter", "--input_path", str(aligned),
            "--output_path", str(aligned_text), "--output_type", "TXT",
        ], 120, self.run_dir / "alignment_text.ok", (aligned_text / "images.txt",))
        alignment_validation = evaluate_metric_alignment(aligned_text / "images.txt", frames)

        self._progress(58, "Undistorting calibrated images")
        undistort_ok = self.run_dir / "undistort.ok"
        if not undistort_ok.exists() or not (dense / "images").is_dir():
            _safe_reset(dense, self.run_dir)
        self._step("undistort", [
            binary, "image_undistorter", "--image_path", str(self.images_dir),
            "--input_path", str(aligned), "--output_path", str(dense),
            "--output_type", "COLMAP", "--max_image_size", str(work_size),
        ], 600, undistort_ok, (dense / "images",))
        self._progress(68, "GPU dense stereo")
        self._step("stereo", [
            binary, "patch_match_stereo", "--workspace_path", str(dense),
            "--workspace_format", "COLMAP", "--PatchMatchStereo.geom_consistency", "true",
            "--PatchMatchStereo.max_image_size", str(work_size),
            "--PatchMatchStereo.num_iterations", str(stereo_iterations),
            "--PatchMatchStereo.num_samples", str(stereo_samples),
        ], 3600, self.run_dir / "stereo.ok", (dense / "stereo" / "depth_maps",))
        fused = dense / "fused.ply"
        self._progress(83, "Dense point fusion")
        mask_dir = make_roi_masks(metadata, self.run_dir / "roi_masks")
        fusion_command = [
            binary, "stereo_fusion", "--workspace_path", str(dense),
            "--workspace_format", "COLMAP", "--input_type", "geometric",
            "--output_path", str(fused),
        ]
        if mask_dir is not None:
            fusion_command += ["--StereoFusion.mask_path", str(mask_dir)]
        if fused.is_file() and fused.stat().st_size < 1000:
            (self.run_dir / "fusion.ok").unlink(missing_ok=True)
        self._step("fusion", fusion_command, 1200, self.run_dir / "fusion.ok", (fused,))
        if not fused.is_file() or fused.stat().st_size < 1000:
            raise ReconstructionError("Dense stereo yielded no usable point cloud")
        mesh_path = dense / "meshed-poisson.ply"
        self._progress(91, "Poisson surface meshing")
        if mesh_path.is_file() and mesh_path.stat().st_size < 1000:
            (self.run_dir / "meshing.ok").unlink(missing_ok=True)
        self._step("meshing", [
            binary, "poisson_mesher", "--input_path", str(fused),
            "--output_path", str(mesh_path), "--PoissonMeshing.depth", str(poisson_depth),
        ], 1800, self.run_dir / "meshing.ok", (mesh_path,))
        if not mesh_path.is_file() or mesh_path.stat().st_size < 1000:
            raise ReconstructionError("Meshing yielded no usable triangles")
        return alignment_validation

    def _export_metric_mesh(self, metadata):
        self.output_dir.mkdir(parents=True, exist_ok=True)
        source = self.run_dir / "dense" / "meshed-poisson.ply"
        mesh = trimesh.load(source, force="mesh", process=False)
        if not isinstance(mesh, trimesh.Trimesh) or len(mesh.faces) < 100:
            raise ReconstructionError("Mesher returned insufficient triangles")
        exports = []
        scene = self.output_dir / f"{self.session_id}_scene_metric.ply"
        shutil.copy2(source, scene)
        exports.append(self._descriptor(scene, "scene_ply_metres"))
        roi = metadata.get("roi")
        if roi:
            center = roi["center"]
            centre = np.array([center["x"], center["y"], -center["z"]], dtype=float)
            radius = float(roi["radius"])
            if not np.isfinite(centre).all() or not math.isfinite(radius) or not 0.01 <= radius <= 5:
                raise ReconstructionError("Invalid object selection volume")
            inside = np.linalg.norm(mesh.vertices - centre, axis=1) <= radius
            face_mask = np.all(inside[mesh.faces], axis=1)
            if np.count_nonzero(face_mask) < 100:
                raise ReconstructionError("Object selection excludes the reconstructed mesh")
            mesh.update_faces(face_mask)
            mesh.remove_unreferenced_vertices()
        if not np.isfinite(mesh.vertices).all():
            raise ReconstructionError("Mesh contains non-finite vertices")
        # Keep the reconstructed editable geometry distinct from print repair.
        bounds_m = (mesh.bounds[1] - mesh.bounds[0]).tolist()
        if min(bounds_m) <= 0 or max(bounds_m) > 10:
            raise ReconstructionError("Implausible physical mesh dimensions")
        validation = {
            "units": "metres", "bounds_m": bounds_m,
            "bounds_mm": (np.asarray(bounds_m) * 1000).tolist(),
            "vertices": len(mesh.vertices), "faces": len(mesh.faces),
            "watertight": bool(mesh.is_watertight),
            "winding_consistent": bool(mesh.is_winding_consistent),
            "volume_m3": float(mesh.volume) if mesh.is_watertight else None,
            "roi_applied": bool(roi),
        }
        for ext in ("obj", "glb", "ply"):
            path = self.output_dir / f"{self.session_id}_object_editable.{ext}"
            mesh.export(path)
            exports.append(self._descriptor(path, f"editable_{ext}_metres"))
        printable = mesh.copy()
        printable.update_faces(printable.nondegenerate_faces())
        printable.update_faces(printable.unique_faces())
        printable.remove_unreferenced_vertices()
        trimesh.repair.fix_normals(printable)
        trimesh.repair.fill_holes(printable)
        validation["repair_candidate_watertight"] = bool(printable.is_watertight)
        validation["repair_candidate_volume_m3"] = float(printable.volume) if printable.is_watertight else None
        if printable.is_volume and min(np.asarray(printable.bounds[1] - printable.bounds[0])) > 0:
            # Editable geometry remains Y-up in metres. The slicer copy is
            # Z-up, centred on the build plate and measured in millimetres.
            printable.apply_transform(trimesh.transformations.rotation_matrix(np.pi / 2, [1, 0, 0]))
            centre_xy = printable.bounds.mean(axis=0)
            printable.apply_translation([-centre_xy[0], -centre_xy[1], -printable.bounds[0, 2]])
            printable.apply_scale(1000.0)
            path = self.output_dir / f"{self.session_id}_print_candidate_mm.stl"
            printable.export(path)
            exports.append(self._descriptor(path, "print_candidate_stl_millimetres"))
        else:
            validation["print_warning"] = "Mesh remains open or invalid; edit before printing"
        atomic_write(self.output_dir / "mesh_validation.json",
                     json.dumps(validation, indent=2).encode("utf-8"))
        return exports, validation

    def _descriptor(self, path, label):
        url = f"/api/sessions/{self.session_id}/exports/{path.name}"
        return {"filename": path.name, "format": path.suffix[1:].upper(),
                "role": label, "path": str(path), "url": url,
                "size_mb": round(path.stat().st_size / (1024 * 1024), 2)}


class SimpleReconstruction:
    @staticmethod
    def reconstruct_from_depth_maps(session_dir, session_id, custom_output_dir=None, progress_cb=None):
        return {"session_id": session_id, "status": "failed",
                "error": "Registered, calibrated metric depth capture is not implemented; use COLMAP RGB reconstruction"}
