"""
MetaScan — 3D Reconstruction Pipeline
COLMAP SfM + Open3D mesh reconstruction.
Exports to OBJ, STL, GLB, PLY formats.
"""
import json
import logging
import shutil
import subprocess
from pathlib import Path

import cv2
import numpy as np

logger = logging.getLogger("metascan.reconstruction")


class ReconstructionPipeline:
    """Full 3D reconstruction pipeline using COLMAP + Open3D."""

    def __init__(self, session_dir: Path, session_id: str):
        self.session_dir = session_dir
        self.session_id = session_id
        self.images_dir = session_dir / "images"
        self.depth_dir = session_dir / "depth"
        self.colmap_dir = session_dir / "colmap"
        self.output_dir = session_dir / "output"
        self.output_dir.mkdir(parents=True, exist_ok=True)

    def run_full_pipeline(self, use_colmap: bool = True) -> dict:
        """Run the complete reconstruction pipeline."""
        logger.info(f"Starting full pipeline for session: {self.session_id}")
        result = {"session_id": self.session_id, "status": "started", "exports": []}

        try:
            # Step 1: Try COLMAP if available
            colmap_success = False
            if use_colmap:
                colmap_success = self._run_colmap()

            # Step 2: Depth-based reconstruction (always available)
            mesh = self._reconstruct_from_depth()

            if mesh is not None:
                # Step 3: Export to multiple formats
                exports = self._export_mesh(mesh)
                result["exports"] = exports
                result["status"] = "completed"
                logger.info(f"Pipeline completed: {len(exports)} exports")
            elif colmap_success:
                result["status"] = "colmap_only"
            else:
                result["status"] = "failed"
                result["error"] = "No reconstruction method succeeded"

        except Exception as e:
            logger.error(f"Pipeline error: {e}", exc_info=True)
            result["status"] = "failed"
            result["error"] = str(e)

        return result

    def _run_colmap(self) -> bool:
        """Run COLMAP Structure-from-Motion."""
        from config import COLMAP_BINARY

        colmap_path = shutil.which(COLMAP_BINARY)
        if colmap_path is None:
            logger.warning("COLMAP not found in PATH. Skipping COLMAP step.")
            return False

        colmap_workspace = self.colmap_dir / "workspace"
        colmap_workspace.mkdir(parents=True, exist_ok=True)
        colmap_db = colmap_workspace / "database.db"
        colmap_sparse = colmap_workspace / "sparse"
        colmap_dense = colmap_workspace / "dense"
        colmap_sparse.mkdir(exist_ok=True)
        colmap_dense.mkdir(exist_ok=True)

        colmap_images_dir = self.colmap_dir / "images"
        if not colmap_images_dir.exists() or len(list(colmap_images_dir.iterdir())) == 0:
            logger.warning("No COLMAP images found. Skipping COLMAP.")
            return False

        try:
            # Feature extraction
            logger.info("COLMAP: Feature extraction...")
            subprocess.run([
                colmap_path, "feature_extractor",
                "--database_path", str(colmap_db),
                "--image_path", str(colmap_images_dir),
                "--ImageReader.single_camera", "1",
                "--ImageReader.camera_model", "PINHOLE",
            ], check=True, capture_output=True, timeout=600)

            # Feature matching
            logger.info("COLMAP: Feature matching...")
            subprocess.run([
                colmap_path, "sequential_matcher",
                "--database_path", str(colmap_db),
            ], check=True, capture_output=True, timeout=600)

            # Sparse reconstruction (with known poses if available)
            logger.info("COLMAP: Sparse reconstruction...")
            known_model = self.colmap_dir
            cameras_txt = known_model / "cameras.txt"

            if cameras_txt.exists():
                subprocess.run([
                    colmap_path, "mapper",
                    "--database_path", str(colmap_db),
                    "--image_path", str(colmap_images_dir),
                    "--output_path", str(colmap_sparse),
                    "--input_path", str(known_model),
                ], check=True, capture_output=True, timeout=1200)
            else:
                subprocess.run([
                    colmap_path, "mapper",
                    "--database_path", str(colmap_db),
                    "--image_path", str(colmap_images_dir),
                    "--output_path", str(colmap_sparse),
                ], check=True, capture_output=True, timeout=1200)

            logger.info("COLMAP: Sparse reconstruction completed")
            return True

        except FileNotFoundError:
            logger.warning("COLMAP binary not found")
            return False
        except subprocess.TimeoutExpired:
            logger.warning("COLMAP timed out")
            return False
        except subprocess.CalledProcessError as e:
            logger.warning(f"COLMAP failed: {e}")
            return False

    def _reconstruct_from_depth(self):
        """Reconstruct mesh from depth maps using Open3D TSDF fusion."""
        try:
            import open3d as o3d
        except ImportError:
            logger.warning("Open3D not installed. Skipping depth-based reconstruction.")
            return None

        from config import (
            TSDF_VOXEL_LENGTH, TSDF_SDF_TRUNC,
            TSDF_DEPTH_SCALE, TSDF_DEPTH_MAX,
            CAMERA_WIDTH, CAMERA_HEIGHT,
            TARGET_FACE_COUNT
        )

        # Load session metadata
        meta_path = self.session_dir / "session_metadata.json"
        if not meta_path.exists():
            logger.warning("No session metadata found")
            return None

        with open(meta_path) as f:
            metadata = json.load(f)

        frames = metadata.get("frames", [])
        intrinsics_data = metadata.get("intrinsics")

        if not intrinsics_data:
            logger.warning("No camera intrinsics available")
            return None

        # Check if we have depth maps
        depth_files = list(self.depth_dir.glob("depth_*.raw"))
        if len(depth_files) == 0:
            logger.info("No depth maps available. Trying point cloud from poses.")
            return self._reconstruct_from_images_only(frames, intrinsics_data)

        # Setup Open3D intrinsics
        o3d_intrinsics = o3d.camera.PinholeCameraIntrinsic(
            width=intrinsics_data["width"],
            height=intrinsics_data["height"],
            fx=intrinsics_data["fx"],
            fy=intrinsics_data["fy"],
            cx=intrinsics_data["cx"],
            cy=intrinsics_data["cy"],
        )

        # TSDF Volume
        logger.info("Starting TSDF fusion...")
        volume = o3d.pipelines.integration.ScalableTSDFVolume(
            voxel_length=TSDF_VOXEL_LENGTH,
            sdf_trunc=TSDF_SDF_TRUNC,
            color_type=o3d.pipelines.integration.TSDFVolumeColorType.RGB8,
        )

        integrated_count = 0
        for frame in frames:
            depth_file = frame.get("depth")
            if depth_file is None:
                continue

            depth_path = self.depth_dir / depth_file
            image_path = self.images_dir / frame["image"]

            if not depth_path.exists() or not image_path.exists():
                continue

            pose = frame.get("pose")
            if pose is None:
                continue

            # Load depth (uint16, mm)
            raw_depth = np.fromfile(str(depth_path), dtype=np.uint16)
            expected_size = CAMERA_WIDTH * CAMERA_HEIGHT
            if raw_depth.size != expected_size:
                # Try to reshape with actual dimensions
                sqrt_size = int(np.sqrt(raw_depth.size))
                if sqrt_size * sqrt_size == raw_depth.size:
                    depth_image = raw_depth.reshape(sqrt_size, sqrt_size)
                else:
                    continue
            else:
                depth_image = raw_depth.reshape(CAMERA_HEIGHT, CAMERA_WIDTH)

            # Load color
            color_bgr = cv2.imread(str(image_path))
            if color_bgr is None:
                continue
            color_rgb = cv2.cvtColor(color_bgr, cv2.COLOR_BGR2RGB)

            # Resize if needed
            if color_rgb.shape[:2] != depth_image.shape[:2]:
                color_rgb = cv2.resize(
                    color_rgb,
                    (depth_image.shape[1], depth_image.shape[0])
                )

            # Create Open3D images
            o3d_depth = o3d.geometry.Image(depth_image.astype(np.float32))
            o3d_color = o3d.geometry.Image(color_rgb)
            rgbd = o3d.geometry.RGBDImage.create_from_color_and_depth(
                o3d_color, o3d_depth,
                depth_scale=TSDF_DEPTH_SCALE,
                depth_trunc=TSDF_DEPTH_MAX,
                convert_rgb_to_intensity=False,
            )

            # Build extrinsic matrix from pose
            extrinsic = self._pose_to_extrinsic(pose)

            # Integrate
            volume.integrate(rgbd, o3d_intrinsics, np.linalg.inv(extrinsic))
            integrated_count += 1

        if integrated_count == 0:
            logger.warning("No frames could be integrated")
            return None

        logger.info(f"TSDF fusion complete: {integrated_count} frames integrated")

        # Extract mesh
        mesh = volume.extract_triangle_mesh()
        mesh.compute_vertex_normals()

        # Simplify if too many faces
        if len(mesh.triangles) > TARGET_FACE_COUNT:
            logger.info(
                f"Simplifying mesh: {len(mesh.triangles)} → {TARGET_FACE_COUNT} faces"
            )
            mesh = mesh.simplify_quadric_decimation(TARGET_FACE_COUNT)
            mesh.compute_vertex_normals()

        return mesh

    def _reconstruct_from_images_only(self, frames: list, intrinsics_data: dict):
        """Create a point cloud from image poses (no depth maps)."""
        try:
            import open3d as o3d
        except ImportError:
            return None

        # Create sparse point cloud from camera positions
        points = []
        colors = []

        for frame in frames:
            pose = frame.get("pose")
            if pose is None:
                continue
            pos = pose["position"]
            points.append([pos["x"], pos["y"], pos["z"]])
            colors.append([0.3, 0.8, 0.3])  # green

        if len(points) < 4:
            return None

        # Create point cloud
        pcd = o3d.geometry.PointCloud()
        pcd.points = o3d.utility.Vector3dVector(np.array(points))
        pcd.colors = o3d.utility.Vector3dVector(np.array(colors))

        # Try Poisson reconstruction from the camera trajectory
        # This produces a basic mesh following the scan path
        logger.info(f"Created sparse point cloud with {len(points)} points")

        # Save point cloud as PLY
        ply_path = self.output_dir / f"{self.session_id}_pointcloud.ply"
        o3d.io.write_point_cloud(str(ply_path), pcd)

        return None  # No mesh from sparse data alone

    def _pose_to_extrinsic(self, pose: dict) -> np.ndarray:
        """Convert pose dict to 4x4 extrinsic matrix."""
        pos = pose["position"]
        rot = pose["rotation"]

        # Quaternion to rotation matrix
        qx, qy, qz, qw = rot["x"], rot["y"], rot["z"], rot["w"]

        r00 = 1 - 2 * (qy * qy + qz * qz)
        r01 = 2 * (qx * qy - qz * qw)
        r02 = 2 * (qx * qz + qy * qw)
        r10 = 2 * (qx * qy + qz * qw)
        r11 = 1 - 2 * (qx * qx + qz * qz)
        r12 = 2 * (qy * qz - qx * qw)
        r20 = 2 * (qx * qz - qy * qw)
        r21 = 2 * (qy * qz + qx * qw)
        r22 = 1 - 2 * (qx * qx + qy * qy)

        extrinsic = np.eye(4)
        extrinsic[:3, :3] = np.array([
            [r00, r01, r02],
            [r10, r11, r12],
            [r20, r21, r22],
        ])
        extrinsic[:3, 3] = [pos["x"], pos["y"], pos["z"]]

        return extrinsic

    def _export_mesh(self, mesh) -> list[dict]:
        """Export mesh to multiple formats."""
        try:
            import open3d as o3d
            import trimesh
        except ImportError as e:
            logger.error(f"Export dependency missing: {e}")
            return []

        from config import EXPORTS_DIR

        export_dir = EXPORTS_DIR / self.session_id
        export_dir.mkdir(parents=True, exist_ok=True)

        exports = []

        # Convert Open3D mesh to trimesh for multi-format export
        vertices = np.asarray(mesh.vertices)
        triangles = np.asarray(mesh.triangles)
        vertex_colors = None
        if mesh.has_vertex_colors():
            vertex_colors = (np.asarray(mesh.vertex_colors) * 255).astype(np.uint8)

        tri_mesh = trimesh.Trimesh(
            vertices=vertices,
            faces=triangles,
            vertex_colors=vertex_colors,
        )

        # Export OBJ
        try:
            obj_path = export_dir / f"{self.session_id}.obj"
            tri_mesh.export(str(obj_path), file_type="obj")
            exports.append({
                "format": "OBJ",
                "filename": obj_path.name,
                "path": str(obj_path),
                "size_mb": round(obj_path.stat().st_size / (1024 * 1024), 2),
                "url": f"/exports/{self.session_id}/{obj_path.name}",
            })
            logger.info(f"Exported OBJ: {obj_path.name}")
        except Exception as e:
            logger.error(f"OBJ export failed: {e}")

        # Export STL
        try:
            stl_path = export_dir / f"{self.session_id}.stl"
            tri_mesh.export(str(stl_path), file_type="stl")
            exports.append({
                "format": "STL",
                "filename": stl_path.name,
                "path": str(stl_path),
                "size_mb": round(stl_path.stat().st_size / (1024 * 1024), 2),
                "url": f"/exports/{self.session_id}/{stl_path.name}",
            })
            logger.info(f"Exported STL: {stl_path.name}")
        except Exception as e:
            logger.error(f"STL export failed: {e}")

        # Export GLB
        try:
            glb_path = export_dir / f"{self.session_id}.glb"
            tri_mesh.export(str(glb_path), file_type="glb")
            exports.append({
                "format": "GLB",
                "filename": glb_path.name,
                "path": str(glb_path),
                "size_mb": round(glb_path.stat().st_size / (1024 * 1024), 2),
                "url": f"/exports/{self.session_id}/{glb_path.name}",
            })
            logger.info(f"Exported GLB: {glb_path.name}")
        except Exception as e:
            logger.error(f"GLB export failed: {e}")

        # Export PLY
        try:
            ply_path = export_dir / f"{self.session_id}.ply"
            o3d.io.write_triangle_mesh(str(ply_path), mesh)
            exports.append({
                "format": "PLY",
                "filename": ply_path.name,
                "path": str(ply_path),
                "size_mb": round(ply_path.stat().st_size / (1024 * 1024), 2),
                "url": f"/exports/{self.session_id}/{ply_path.name}",
            })
            logger.info(f"Exported PLY: {ply_path.name}")
        except Exception as e:
            logger.error(f"PLY export failed: {e}")

        return exports

    def run_colmap_pipeline(self, export: bool = True) -> dict:
        """Run only COLMAP pipeline."""
        success = self._run_colmap()
        return {
            "session_id": self.session_id,
            "status": "completed" if success else "failed",
            "method": "colmap",
        }


class SimpleReconstruction:
    """Simplified reconstruction using only depth maps (no COLMAP)."""

    @staticmethod
    def reconstruct_from_depth_maps(session_dir: Path, session_id: str) -> dict:
        """Quick TSDF reconstruction from depth maps only."""
        pipeline = ReconstructionPipeline(session_dir, session_id)
        return pipeline.run_full_pipeline(use_colmap=False)
