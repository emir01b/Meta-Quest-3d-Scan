"""
Meta3D Scanner - 3D Reconstruction Pipeline
Supports dual pipeline: COLMAP MVS (geometric) + Nerfstudio Gaussian Splatting (neural)
"""
import json
import subprocess
import logging
import shutil
import time
from pathlib import Path

import cv2
import numpy as np

from config import (
    COLMAP_BINARY, COLMAP_CAMERA_MODEL, SFM_MAX_NUM_FEATURES,
    MATCH_TYPE, POISSON_DEPTH, POISSON_TRIM, VOXEL_SIZE,
    MESH_SIMPLIFY_TARGET, NERFSTUDIO_METHOD, NERFSTUDIO_MAX_STEPS,
    EXPORTS_DIR, COLMAP_MAX_IMAGE_SIZE, FRAME_FORMAT
)

logger = logging.getLogger("meta3d.reconstruction")


class ReconstructionPipeline:
    """
    Dual 3D reconstruction pipeline for ultra detail quality.
    Pipeline A: COLMAP SfM → COLMAP MVS → Poisson/TSDF Mesh
    Pipeline B: COLMAP SfM → Nerfstudio Gaussian Splatting → Neural Mesh
    """

    def __init__(self, session_dir: Path, session_id: str):
        self.session_dir = session_dir
        self.session_id = session_id
        self.images_dir = session_dir / "images"
        self.colmap_dir = session_dir / "colmap"
        self.export_dir = EXPORTS_DIR / session_id
        self.export_dir.mkdir(parents=True, exist_ok=True)
        self.progress_callback = None

    def set_progress_callback(self, callback):
        """Set callback for progress updates: callback(stage, progress, message)"""
        self.progress_callback = callback

    def _report_progress(self, stage: str, progress: float, message: str):
        """Report progress to callback."""
        logger.info(f"[{stage}] {progress:.0%} - {message}")
        if self.progress_callback:
            self.progress_callback(stage, progress, message)

    # =========================================================================
    # PIPELINE A: COLMAP Full Pipeline (Best geometric accuracy)
    # =========================================================================

    def run_colmap_pipeline(self, use_known_poses: bool = True) -> dict:
        """
        Run full COLMAP reconstruction pipeline.
        
        Steps:
        1. Feature extraction
        2. Feature matching
        3. Sparse reconstruction (SfM) or use known poses
        4. Image undistortion
        5. Dense reconstruction (MVS - PatchMatch Stereo)
        6. Poisson mesh reconstruction
        """
        self._report_progress("colmap", 0.0, "Starting COLMAP pipeline...")

        colmap_ws = self.colmap_dir
        colmap_ws.mkdir(parents=True, exist_ok=True)
        images_path = colmap_ws / "images"
        database_path = colmap_ws / "database.db"
        sparse_path = colmap_ws / "sparse"
        dense_path = colmap_ws / "dense"

        # Ensure images are available
        if not images_path.exists():
            images_path.mkdir(parents=True, exist_ok=True)
            for img in self.images_dir.glob(f"*.{FRAME_FORMAT}"):
                shutil.copy2(str(img), str(images_path / img.name))

        results = {}

        try:
            # Step 1: Feature Extraction
            self._report_progress("colmap", 0.1, "Extracting features...")
            self._run_colmap_cmd([
                COLMAP_BINARY, "feature_extractor",
                "--database_path", str(database_path),
                "--image_path", str(images_path),
                "--ImageReader.camera_model", COLMAP_CAMERA_MODEL,
                "--ImageReader.single_camera", "1",
                "--SiftExtraction.max_image_size", str(COLMAP_MAX_IMAGE_SIZE),
                "--SiftExtraction.max_num_features", str(SFM_MAX_NUM_FEATURES),
                "--SiftExtraction.estimate_affine_shape", "1",
                "--SiftExtraction.domain_size_pooling", "1",
            ])

            # Step 2: Feature Matching
            self._report_progress("colmap", 0.25, "Matching features...")
            if MATCH_TYPE == "exhaustive":
                self._run_colmap_cmd([
                    COLMAP_BINARY, "exhaustive_matcher",
                    "--database_path", str(database_path),
                    "--SiftMatching.guided_matching", "1",
                ])
            else:
                self._run_colmap_cmd([
                    COLMAP_BINARY, "sequential_matcher",
                    "--database_path", str(database_path),
                    "--SiftMatching.guided_matching", "1",
                ])

            # Step 3: Sparse Reconstruction
            if use_known_poses and (sparse_path / "0" / "images.txt").exists():
                self._report_progress("colmap", 0.35, "Using known camera poses...")
                # Triangulate points using known poses
                self._run_colmap_cmd([
                    COLMAP_BINARY, "point_triangulator",
                    "--database_path", str(database_path),
                    "--image_path", str(images_path),
                    "--input_path", str(sparse_path / "0"),
                    "--output_path", str(sparse_path / "0"),
                ])
            else:
                self._report_progress("colmap", 0.35, "Running SfM (mapper)...")
                sparse_path.mkdir(parents=True, exist_ok=True)
                self._run_colmap_cmd([
                    COLMAP_BINARY, "mapper",
                    "--database_path", str(database_path),
                    "--image_path", str(images_path),
                    "--output_path", str(sparse_path),
                    "--Mapper.ba_global_max_num_iterations", "50",
                    "--Mapper.ba_global_max_refinements", "5",
                ])

            results["sparse_model"] = str(sparse_path / "0")
            self._report_progress("colmap", 0.4, "Sparse reconstruction complete")

            # Step 4: Image Undistortion
            self._report_progress("colmap", 0.45, "Undistorting images...")
            dense_path.mkdir(parents=True, exist_ok=True)
            self._run_colmap_cmd([
                COLMAP_BINARY, "image_undistorter",
                "--image_path", str(images_path),
                "--input_path", str(sparse_path / "0"),
                "--output_path", str(dense_path),
                "--output_type", "COLMAP",
                "--max_image_size", str(COLMAP_MAX_IMAGE_SIZE),
            ])

            # Step 5: Dense Reconstruction (Stereo)
            self._report_progress("colmap", 0.55, "Running PatchMatch Stereo (densification)...")
            self._run_colmap_cmd([
                COLMAP_BINARY, "patch_match_stereo",
                "--workspace_path", str(dense_path),
                "--workspace_format", "COLMAP",
                "--PatchMatchStereo.geom_consistency", "true",
                "--PatchMatchStereo.max_image_size", str(COLMAP_MAX_IMAGE_SIZE),
            ])

            # Step 6: Stereo Fusion (Dense Point Cloud)
            self._report_progress("colmap", 0.7, "Fusing stereo results...")
            fused_ply = dense_path / "fused.ply"
            self._run_colmap_cmd([
                COLMAP_BINARY, "stereo_fusion",
                "--workspace_path", str(dense_path),
                "--workspace_format", "COLMAP",
                "--input_type", "geometric",
                "--output_path", str(fused_ply),
            ])

            results["dense_point_cloud"] = str(fused_ply)
            self._report_progress("colmap", 0.75, "Dense point cloud created")

            # Step 7: Poisson Mesh Reconstruction
            self._report_progress("colmap", 0.8, "Running Poisson mesh reconstruction...")
            mesh_ply = dense_path / "meshed-poisson.ply"
            self._run_colmap_cmd([
                COLMAP_BINARY, "poisson_mesher",
                "--input_path", str(fused_ply),
                "--output_path", str(mesh_ply),
                "--PoissonMeshing.depth", str(POISSON_DEPTH),
                "--PoissonMeshing.trim", str(POISSON_TRIM),
            ])

            results["mesh_ply"] = str(mesh_ply)

            # Step 8: Delaunay mesh (alternative)
            delaunay_ply = dense_path / "meshed-delaunay.ply"
            self._run_colmap_cmd([
                COLMAP_BINARY, "delaunay_mesher",
                "--input_path", str(dense_path),
                "--output_path", str(delaunay_ply),
            ])
            results["mesh_delaunay"] = str(delaunay_ply)

            self._report_progress("colmap", 0.9, "Mesh reconstruction complete")

            # Step 9: Export to OBJ/GLB
            self._report_progress("colmap", 0.95, "Exporting final models...")
            export_results = self._export_mesh(mesh_ply, "colmap")
            results.update(export_results)

            self._report_progress("colmap", 1.0, "COLMAP pipeline complete!")
            results["status"] = "success"

        except Exception as e:
            logger.error(f"COLMAP pipeline error: {e}", exc_info=True)
            results["status"] = "error"
            results["error"] = str(e)

        return results

    # =========================================================================
    # PIPELINE B: Nerfstudio Pipeline (Best visual quality)
    # =========================================================================

    def run_nerfstudio_pipeline(self) -> dict:
        """
        Run Nerfstudio reconstruction pipeline.
        
        Steps:
        1. Process data (convert to nerfstudio format)
        2. Train model (Gaussian Splatting / NeRF)
        3. Export mesh
        """
        self._report_progress("nerfstudio", 0.0, "Starting Nerfstudio pipeline...")
        results = {}
        ns_data_dir = self.session_dir / "nerfstudio_data"
        ns_output_dir = self.session_dir / "nerfstudio_output"

        try:
            # Step 1: Convert COLMAP data to Nerfstudio format
            self._report_progress("nerfstudio", 0.05, "Processing data for Nerfstudio...")

            colmap_sparse = self.colmap_dir / "sparse" / "0"
            colmap_images = self.colmap_dir / "images"

            if not colmap_sparse.exists():
                results["status"] = "error"
                results["error"] = "COLMAP sparse model not found. Run COLMAP pipeline first."
                return results

            self._run_cmd([
                "ns-process-data", "images",
                "--data", str(colmap_images),
                "--output-dir", str(ns_data_dir),
                "--colmap-model-path", str(colmap_sparse),
                "--skip-colmap",
                "--skip-image-processing",
            ])

            # Step 2: Train model
            self._report_progress("nerfstudio", 0.15, f"Training {NERFSTUDIO_METHOD}...")
            model_output = ns_output_dir / NERFSTUDIO_METHOD
            self._run_cmd([
                "ns-train", NERFSTUDIO_METHOD,
                "--data", str(ns_data_dir),
                "--output-dir", str(ns_output_dir),
                "--max-num-iterations", str(NERFSTUDIO_MAX_STEPS),
                "--pipeline.model.num-downscales", "0",
                "--viewer.quit-on-train-completion", "True",
                "nerfstudio-data",
                "--data", str(ns_data_dir),
            ])

            self._report_progress("nerfstudio", 0.7, "Training complete")

            # Find latest checkpoint
            config_path = self._find_latest_config(ns_output_dir)
            if not config_path:
                results["status"] = "error"
                results["error"] = "Could not find trained model config"
                return results

            results["config_path"] = str(config_path)

            # Step 3: Export mesh
            self._report_progress("nerfstudio", 0.75, "Exporting mesh from neural model...")
            ns_mesh_dir = self.session_dir / "nerfstudio_mesh"
            ns_mesh_dir.mkdir(exist_ok=True)

            self._run_cmd([
                "ns-export", "poisson",
                "--load-config", str(config_path),
                "--output-dir", str(ns_mesh_dir),
                "--target-num-faces", str(MESH_SIMPLIFY_TARGET),
                "--num-points", "1000000",
                "--remove-outliers", "True",
            ])

            # Also export Gaussian Splat if method supports it
            if NERFSTUDIO_METHOD == "splatfacto":
                splat_dir = self.session_dir / "nerfstudio_splat"
                splat_dir.mkdir(exist_ok=True)
                self._run_cmd([
                    "ns-export", "gaussian-splat",
                    "--load-config", str(config_path),
                    "--output-dir", str(splat_dir),
                ])
                results["gaussian_splat"] = str(splat_dir)

            self._report_progress("nerfstudio", 0.9, "Exporting to standard formats...")

            # Find and convert the mesh
            mesh_file = self._find_mesh_file(ns_mesh_dir)
            if mesh_file:
                export_results = self._export_mesh(mesh_file, "nerfstudio")
                results.update(export_results)

            self._report_progress("nerfstudio", 1.0, "Nerfstudio pipeline complete!")
            results["status"] = "success"

        except Exception as e:
            logger.error(f"Nerfstudio pipeline error: {e}", exc_info=True)
            results["status"] = "error"
            results["error"] = str(e)

        return results

    # =========================================================================
    # COMBINED PIPELINE (Ultra Detail)
    # =========================================================================

    def run_full_pipeline(self, use_known_poses: bool = True) -> dict:
        """
        Run both pipelines for ultra detail quality.
        Returns results from both pipelines.
        """
        self._report_progress("full", 0.0, "Starting ultra detail dual pipeline...")

        results = {
            "session_id": self.session_id,
            "pipelines": {}
        }

        # Pipeline A: COLMAP (geometrically accurate)
        self._report_progress("full", 0.05, "Running COLMAP pipeline (geometric accuracy)...")
        colmap_results = self.run_colmap_pipeline(use_known_poses)
        results["pipelines"]["colmap"] = colmap_results

        # Pipeline B: Nerfstudio (visual quality)
        if colmap_results.get("status") == "success":
            self._report_progress("full", 0.5, "Running Nerfstudio pipeline (visual quality)...")
            ns_results = self.run_nerfstudio_pipeline()
            results["pipelines"]["nerfstudio"] = ns_results
        else:
            self._report_progress("full", 0.5,
                                  "COLMAP failed, skipping Nerfstudio (requires COLMAP output)")
            results["pipelines"]["nerfstudio"] = {
                "status": "skipped",
                "reason": "COLMAP pipeline failed"
            }

        # Final summary
        results["export_dir"] = str(self.export_dir)
        self._report_progress("full", 1.0, "All pipelines complete!")

        # Save results
        with open(self.export_dir / "reconstruction_results.json", "w") as f:
            json.dump(results, f, indent=2)

        return results

    # =========================================================================
    # Utility Methods
    # =========================================================================

    def _export_mesh(self, mesh_path: Path, pipeline_name: str) -> dict:
        """Export mesh to OBJ and GLB formats using Open3D and trimesh."""
        results = {}
        try:
            import open3d as o3d
            import trimesh

            mesh_path = Path(mesh_path)
            if not mesh_path.exists():
                logger.warning(f"Mesh file not found: {mesh_path}")
                return results

            # Load mesh
            o3d_mesh = o3d.io.read_triangle_mesh(str(mesh_path))

            if not o3d_mesh.has_vertex_normals():
                o3d_mesh.compute_vertex_normals()

            # Simplify if needed
            if len(o3d_mesh.triangles) > MESH_SIMPLIFY_TARGET:
                logger.info(f"Simplifying mesh from {len(o3d_mesh.triangles)} "
                            f"to {MESH_SIMPLIFY_TARGET} faces...")
                o3d_mesh = o3d_mesh.simplify_quadric_decimation(MESH_SIMPLIFY_TARGET)
                o3d_mesh.compute_vertex_normals()

            # Export OBJ
            obj_path = self.export_dir / f"{self.session_id}_{pipeline_name}.obj"
            o3d.io.write_triangle_mesh(str(obj_path), o3d_mesh)
            results[f"{pipeline_name}_obj"] = str(obj_path)
            logger.info(f"OBJ exported: {obj_path}")

            # Export PLY (with colors)
            ply_path = self.export_dir / f"{self.session_id}_{pipeline_name}.ply"
            o3d.io.write_triangle_mesh(str(ply_path), o3d_mesh)
            results[f"{pipeline_name}_ply"] = str(ply_path)

            # Export GLB using trimesh
            try:
                tri_mesh = trimesh.load(str(obj_path))
                glb_path = self.export_dir / f"{self.session_id}_{pipeline_name}.glb"
                tri_mesh.export(str(glb_path))
                results[f"{pipeline_name}_glb"] = str(glb_path)
                logger.info(f"GLB exported: {glb_path}")
            except Exception as e:
                logger.warning(f"GLB export failed: {e}")

        except ImportError as e:
            logger.warning(f"Export dependency not available: {e}")
        except Exception as e:
            logger.error(f"Mesh export error: {e}", exc_info=True)

        return results

    def _run_colmap_cmd(self, cmd: list[str]):
        """Run a COLMAP command."""
        logger.info(f"Running: {' '.join(cmd[:3])}...")
        result = subprocess.run(
            cmd, capture_output=True, text=True, timeout=3600
        )
        if result.returncode != 0:
            logger.error(f"COLMAP error:\n{result.stderr}")
            raise RuntimeError(f"COLMAP command failed: {result.stderr[:500]}")
        return result

    def _run_cmd(self, cmd: list[str]):
        """Run a general command."""
        logger.info(f"Running: {' '.join(cmd[:3])}...")
        result = subprocess.run(
            cmd, capture_output=True, text=True, timeout=7200
        )
        if result.returncode != 0:
            logger.error(f"Command error:\n{result.stderr}")
            raise RuntimeError(f"Command failed: {result.stderr[:500]}")
        return result

    def _find_latest_config(self, output_dir: Path) -> Path | None:
        """Find the latest Nerfstudio config file."""
        config_files = list(output_dir.rglob("config.yml"))
        if not config_files:
            return None
        # Sort by modification time, newest first
        return sorted(config_files, key=lambda p: p.stat().st_mtime, reverse=True)[0]

    def _find_mesh_file(self, mesh_dir: Path) -> Path | None:
        """Find a mesh file in the output directory."""
        for ext in [".ply", ".obj", ".stl"]:
            files = list(mesh_dir.rglob(f"*{ext}"))
            if files:
                return files[0]
        return None


class SimpleReconstruction:
    """
    Fallback reconstruction using only Open3D (no COLMAP/Nerfstudio required).
    Useful for quick preview or when external tools aren't installed.
    """

    @staticmethod
    def reconstruct_from_depth_maps(session_dir: Path, session_id: str) -> dict:
        """
        Reconstruct using depth maps and known poses via TSDF fusion.
        This doesn't require COLMAP - uses the Quest's depth sensor directly.
        """
        import open3d as o3d

        images_dir = session_dir / "images"
        depth_dir = session_dir / "depth"
        metadata_dir = session_dir / "metadata"
        export_dir = EXPORTS_DIR / session_id
        export_dir.mkdir(parents=True, exist_ok=True)

        # Load metadata
        metadata_files = sorted(metadata_dir.glob("*.json"))
        if not metadata_files:
            return {"status": "error", "error": "No metadata found"}

        # Initialize TSDF volume
        volume = o3d.pipelines.integration.ScalableTSDFVolume(
            voxel_length=VOXEL_SIZE,
            sdf_trunc=VOXEL_SIZE * 5,
            color_type=o3d.pipelines.integration.TSDFVolumeColorType.RGB8,
        )

        frame_count = 0
        for meta_file in metadata_files:
            with open(meta_file) as f:
                meta = json.load(f)

            frame_name = meta["frame_name"]
            color_path = images_dir / f"{frame_name}.{FRAME_FORMAT}"
            depth_path = depth_dir / f"{frame_name}_depth.npy"

            if not color_path.exists() or not depth_path.exists():
                continue

            # Load images
            color = o3d.io.read_image(str(color_path))
            depth_np = np.load(str(depth_path)).astype(np.float32)
            # Convert depth from mm to meters if needed
            if depth_np.max() > 100:
                depth_np = depth_np / 1000.0
            depth = o3d.geometry.Image(depth_np)

            # Create RGBD image
            rgbd = o3d.geometry.RGBDImage.create_from_color_and_depth(
                color, depth,
                depth_scale=1.0,
                depth_trunc=3.0,
                convert_rgb_to_intensity=False,
            )

            # Camera intrinsics
            intrinsic = o3d.camera.PinholeCameraIntrinsic(
                meta["width"], meta["height"],
                meta["focal_length_x"], meta["focal_length_y"],
                meta["principal_point_x"], meta["principal_point_y"],
            )

            # Camera pose
            pose = np.array(meta["pose_matrix"]).reshape(4, 4)
            extrinsic = np.linalg.inv(pose)

            # Integrate
            volume.integrate(rgbd, intrinsic, extrinsic)
            frame_count += 1

        if frame_count == 0:
            return {"status": "error", "error": "No valid frames with depth data"}

        # Extract mesh
        mesh = volume.extract_triangle_mesh()
        mesh.compute_vertex_normals()

        # Save
        mesh_path = export_dir / f"{session_id}_tsdf.ply"
        o3d.io.write_triangle_mesh(str(mesh_path), mesh)

        # Also export OBJ
        obj_path = export_dir / f"{session_id}_tsdf.obj"
        o3d.io.write_triangle_mesh(str(obj_path), mesh)

        return {
            "status": "success",
            "mesh_ply": str(mesh_path),
            "mesh_obj": str(obj_path),
            "frame_count": frame_count,
            "vertices": len(mesh.vertices),
            "triangles": len(mesh.triangles),
        }
