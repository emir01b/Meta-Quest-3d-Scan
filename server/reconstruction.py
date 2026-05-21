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
from scipy.spatial.transform import Rotation

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
        from config import (
            COLMAP_BINARY, COLMAP_USE_KNOWN_POSES, COLMAP_FIX_INTRINSICS
        )

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

        # Load known intrinsics from session (if available)
        intrinsics_path = self.session_dir / "intrinsics.json"
        camera_params = None
        if intrinsics_path.exists():
            with open(intrinsics_path) as f:
                intr = json.load(f)
            camera_params = f"{intr['fx']},{intr['fy']},{intr['cx']},{intr['cy']}"
            logger.info(f"Using known intrinsics: {camera_params}")

        try:
            # ── Step 1: Feature extraction (with known intrinsics if available) ──
            logger.info("COLMAP: Feature extraction...")
            feat_cmd = [
                colmap_path, "feature_extractor",
                "--database_path", str(colmap_db),
                "--image_path", str(colmap_images_dir),
                "--ImageReader.single_camera", "1",
                "--ImageReader.camera_model", "PINHOLE",
                # ── Enhanced SIFT for low-contrast passthrough images ──
                "--SiftExtraction.max_num_features", "8192",
                "--SiftExtraction.first_octave", "-1",        # Sub-pixel features
                "--SiftExtraction.peak_threshold", "0.004",   # Lower peak threshold → more features
                "--SiftExtraction.edge_threshold", "16",      # Allow weaker edge features
                "--SiftExtraction.domain_size_pooling", "1",  # Better descriptor quality
            ]
            if camera_params and COLMAP_FIX_INTRINSICS:
                feat_cmd += ["--ImageReader.camera_params", camera_params]
            subprocess.run(feat_cmd, check=True, capture_output=True, timeout=600)

            # ── Step 2: Feature matching (exhaustive for better coverage) ──
            logger.info("COLMAP: Exhaustive feature matching...")
            subprocess.run([
                colmap_path, "exhaustive_matcher",
                "--database_path", str(colmap_db),
                "--ExhaustiveMatching.block_size", "50",
            ], check=True, capture_output=True, timeout=1200)

            # ── Step 3: Reconstruction ──
            known_model_dir = self.colmap_dir  # Contains cameras.txt, images.txt, points3D.txt
            cameras_txt = known_model_dir / "cameras.txt"
            images_txt = known_model_dir / "images.txt"

            if COLMAP_USE_KNOWN_POSES and cameras_txt.exists() and images_txt.exists():
                # ── 3a: Convert text model to binary (required by point_triangulator) ──
                bin_model_dir = colmap_workspace / "known_model_bin"
                bin_model_dir.mkdir(exist_ok=True)
                logger.info("COLMAP: Converting text model to binary...")
                subprocess.run([
                    colmap_path, "model_converter",
                    "--input_path", str(known_model_dir),
                    "--output_path", str(bin_model_dir),
                    "--output_type", "BIN",
                ], check=True, capture_output=True, timeout=120)

                reconstruction_success = False
                # ── 3b: point_triangulator — keeps Quest IMU poses fixed ──
                try:
                    logger.info("COLMAP: Point triangulation with known poses...")
                    subprocess.run([
                        colmap_path, "point_triangulator",
                        "--database_path", str(colmap_db),
                        "--image_path", str(colmap_images_dir),
                        "--input_path", str(bin_model_dir),
                        "--output_path", str(colmap_sparse),
                    ], check=True, capture_output=True, timeout=1200)
                    logger.info("COLMAP: Point triangulation completed (poses fixed)")
                    reconstruction_success = True
                except subprocess.CalledProcessError as e:
                    logger.warning(f"point_triangulator failed: {e}. Falling back to mapper.")

                if not reconstruction_success:
                    # ── 3c: Fallback — mapper with known poses as initialization ──
                    logger.info("COLMAP: Mapper with known poses as prior...")
                    mapper_cmd = [
                        colmap_path, "mapper",
                        "--database_path", str(colmap_db),
                        "--image_path", str(colmap_images_dir),
                        "--output_path", str(colmap_sparse),
                        "--input_path", str(bin_model_dir),
                    ]
                    if COLMAP_FIX_INTRINSICS:
                        mapper_cmd += [
                            "--Mapper.ba_refine_focal_length", "0",
                            "--Mapper.ba_refine_extra_params", "0",
                        ]
                    subprocess.run(mapper_cmd, check=True, capture_output=True, timeout=1200)
            else:
                # ── No known poses — full mapper from scratch ──
                logger.info("COLMAP: Mapper from scratch (no known poses)...")
                subprocess.run([
                    colmap_path, "mapper",
                    "--database_path", str(colmap_db),
                    "--image_path", str(colmap_images_dir),
                    "--output_path", str(colmap_sparse),
                ], check=True, capture_output=True, timeout=1200)
            # ── Export sparse reconstruction to PLY ──
            try:
                ply_path = colmap_workspace / "model.ply"
                
                # Mapper puts it in '0/', point_triangulator puts it directly in sparse/
                input_dir = colmap_sparse / "0"
                if not input_dir.exists():
                    input_dir = colmap_sparse
                    
                if (input_dir / "points3D.bin").exists() or (input_dir / "points3D.txt").exists():
                    logger.info(f"COLMAP: Exporting sparse reconstruction to PLY from {input_dir}...")
                    subprocess.run([
                        colmap_path, "model_converter",
                        "--input_path", str(input_dir),
                        "--output_path", str(ply_path),
                        "--output_type", "PLY"
                    ], check=True, capture_output=True, timeout=120)
                    logger.info("COLMAP: Sparse reconstruction exported to PLY")
                else:
                    logger.warning(f"No COLMAP points3D found in {input_dir}")
            except Exception as e:
                logger.warning(f"Failed to export COLMAP PLY: {e}")

            # ── Dense reconstruction: undistort → patch_match → fusion ──
            try:
                # Find the sparse model directory (mapper uses 0/, triangulator uses sparse/)
                sparse_model_dir = colmap_sparse / "0"
                if not sparse_model_dir.exists():
                    sparse_model_dir = colmap_sparse

                # Step A: Undistort images
                logger.info("COLMAP: Undistorting images for dense reconstruction...")
                subprocess.run([
                    colmap_path, "image_undistorter",
                    "--image_path", str(colmap_images_dir),
                    "--input_path", str(sparse_model_dir),
                    "--output_path", str(colmap_dense),
                    "--output_type", "COLMAP",
                ], check=True, capture_output=True, timeout=300)

                # Step B: Stereo matching (depth estimation per image pair)
                logger.info("COLMAP: Patch match stereo (dense depth estimation)...")
                patch_cmd = [
                    colmap_path, "patch_match_stereo",
                    "--workspace_path", str(colmap_dense),
                    "--workspace_format", "COLMAP",
                    "--PatchMatchStereo.geom_consistency", "true",
                    "--PatchMatchStereo.max_image_size", "1280",
                ]
                # Try GPU first, fall back to CPU-compatible settings
                subprocess.run(patch_cmd, check=True, capture_output=True, timeout=1800)

                # Step C: Stereo fusion (merge depth maps into dense point cloud)
                dense_ply_path = colmap_dense / "fused.ply"
                logger.info("COLMAP: Stereo fusion (merging depth maps)...")
                subprocess.run([
                    colmap_path, "stereo_fusion",
                    "--workspace_path", str(colmap_dense),
                    "--workspace_format", "COLMAP",
                    "--output_path", str(dense_ply_path),
                    "--StereoFusion.min_num_pixels", "3",
                ], check=True, capture_output=True, timeout=600)

                if dense_ply_path.exists() and dense_ply_path.stat().st_size > 1000:
                    logger.info(f"COLMAP: Dense point cloud created: {dense_ply_path}")
                else:
                    logger.warning("COLMAP: Dense fusion produced empty or tiny output")

            except subprocess.CalledProcessError as e:
                stderr_text = e.stderr.decode('utf-8', errors='replace') if e.stderr else ''
                logger.warning(f"COLMAP dense reconstruction failed: {e}. stderr: {stderr_text[:500]}")
            except subprocess.TimeoutExpired:
                logger.warning("COLMAP dense reconstruction timed out")
            except Exception as e:
                logger.warning(f"COLMAP dense reconstruction error: {e}")

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
            logger.info("No depth maps available. Checking for COLMAP reconstruction.")
            
            # Check dense PLY first (much better quality)
            dense_ply = self.colmap_dir / "workspace" / "dense" / "fused.ply"
            colmap_ply = self.colmap_dir / "workspace" / "model.ply"
            
            ply_to_use = None
            if dense_ply.exists() and dense_ply.stat().st_size > 1000:
                ply_to_use = dense_ply
                logger.info("Using COLMAP dense point cloud (fused.ply)")
            elif colmap_ply.exists():
                ply_to_use = colmap_ply
                logger.info("Using COLMAP sparse point cloud (model.ply)")
            
            if ply_to_use is not None:
                try:
                    pcd = o3d.io.read_point_cloud(str(ply_to_use))
                    logger.info(f"Loaded point cloud: {len(pcd.points)} points")
                    
                    # COLMAP sometimes exports pitch-black colors
                    if pcd.has_colors():
                        if np.mean(np.asarray(pcd.colors)) < 0.01:
                            logger.info("Stripping black colors to allow viewer fallback color.")
                            pcd.colors = o3d.utility.Vector3dVector()
                            
                    # Remove outliers
                    if len(pcd.points) > 100:
                        logger.info(f"Removing outliers from {len(pcd.points)} points...")
                        nb = min(50, max(10, len(pcd.points) // 20))
                        pcd, ind = pcd.remove_statistical_outlier(nb_neighbors=nb, std_ratio=1.5)
                        logger.info(f"Cleaned point cloud has {len(pcd.points)} points.")
                    
                    # Try Poisson surface reconstruction if we have enough points
                    if len(pcd.points) > 5000:
                        try:
                            logger.info("Attempting Poisson surface reconstruction...")
                            # Estimate normals
                            pcd.estimate_normals(
                                search_param=o3d.geometry.KDTreeSearchParamHybrid(radius=0.02, max_nn=30)
                            )
                            pcd.orient_normals_consistent_tangent_plane(k=15)
                            
                            mesh, densities = o3d.geometry.TriangleMesh.create_from_point_cloud_poisson(
                                pcd, depth=9, width=0, scale=1.1, linear_fit=False
                            )
                            
                            # Remove low-density vertices (noise)
                            densities_np = np.asarray(densities)
                            density_threshold = np.quantile(densities_np, 0.05)
                            vertices_to_remove = densities_np < density_threshold
                            mesh.remove_vertices_by_mask(vertices_to_remove)
                            mesh.compute_vertex_normals()
                            
                            if len(mesh.triangles) > 100:
                                logger.info(f"Poisson mesh: {len(mesh.vertices)} vertices, {len(mesh.triangles)} faces")
                                return mesh
                            else:
                                logger.warning("Poisson mesh too sparse, returning point cloud")
                        except Exception as e:
                            logger.warning(f"Poisson reconstruction failed: {e}")
                    
                    return pcd
                except Exception as e:
                    logger.warning(f"Failed to load COLMAP PLY: {e}")
            
            logger.info("Trying point cloud from poses.")
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

            # Build world-to-camera extrinsic matrix from pose
            extrinsic = self._pose_to_extrinsic(pose)

            # Integrate (extrinsic is already world-to-camera)
            volume.integrate(rgbd, o3d_intrinsics, extrinsic)
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

        # Ensure it also gets copied to EXPORTS so the user can see it in the viewer
        from config import EXPORTS_DIR
        export_dir = EXPORTS_DIR / self.session_id
        export_dir.mkdir(parents=True, exist_ok=True)
        export_ply_path = export_dir / f"{self.session_id}_trajectory.ply"
        import shutil
        shutil.copy2(str(ply_path), str(export_ply_path))

        # We return None for mesh, but run_full_pipeline will fail if mesh is None and colmap failed.
        # We need a way to tell the pipeline we exported SOMETHING.
        return pcd  # Return the pointcloud itself as a fallback!

    def _pose_to_extrinsic(self, pose: dict) -> np.ndarray:
        """
        Unity camera-to-world → Open3D world-to-camera extrinsic matrix.

        Applies:
        1. Coordinate conversion: Unity (left-handed, Y-up) → Open3D (right-handed, Y-down)
        2. Pose inversion: camera-to-world → world-to-camera

        Returns 4x4 world-to-camera matrix ready for Open3D integrate().
        """
        pos = pose["position"]
        rot = pose["rotation"]

        # Unity left-handed → right-handed: flip Y
        qx = -rot["x"]
        qy =  rot["y"]
        qz = -rot["z"]
        qw =  rot["w"]

        px =  pos["x"]
        py = -pos["y"]
        pz =  pos["z"]

        # Rotation matrix (camera-to-world in right-handed coords)
        R_c2w = Rotation.from_quat([qx, qy, qz, qw]).as_matrix()
        t_c2w = np.array([px, py, pz])

        # Invert to world-to-camera (what Open3D expects)
        R_w2c = R_c2w.T
        t_w2c = -R_w2c @ t_c2w

        extrinsic = np.eye(4)
        extrinsic[:3, :3] = R_w2c
        extrinsic[:3,  3] = t_w2c

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

        # Determine if it's a mesh or point cloud
        is_pointcloud = not hasattr(mesh, 'triangles')

        if is_pointcloud:
            try:
                ply_path = export_dir / f"{self.session_id}_points.ply"
                o3d.io.write_point_cloud(str(ply_path), mesh)
                exports.append({
                    "format": "PLY",
                    "filename": ply_path.name,
                    "path": str(ply_path),
                    "size_mb": round(ply_path.stat().st_size / (1024 * 1024), 2),
                    "url": f"/exports/{self.session_id}/{ply_path.name}",
                })
                logger.info(f"Exported PLY point cloud: {ply_path.name}")
            except Exception as e:
                logger.error(f"PLY export failed: {e}")
            return exports

        # It's a triangle mesh, convert Open3D mesh to trimesh for multi-format export
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
