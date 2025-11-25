/*
 * Lone EFT DMA Radar
 * MIT License - Copyright (c) 2025 Lone DMA
 */

using LoneEftDmaRadar.DMA;
using LoneEftDmaRadar.Misc;
using LoneEftDmaRadar.Tarkov.GameWorld.Player;
using LoneEftDmaRadar.Tarkov.Mono.Collections;
using LoneEftDmaRadar.Tarkov.Unity;
using LoneEftDmaRadar.Tarkov.Unity.Structures;
using SkiaSharp;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using VmmSharpEx;
using VmmSharpEx.Scatter;

namespace LoneEftDmaRadar.Tarkov.GameWorld.Camera
{
    public sealed class CameraManager
    {
        private static CameraManager _current;
        public static CameraManager Current => _current;
    
        private Thread _initThread;
        private bool _initThreadRunning;
    
        public ulong FPSCamera { get; private set; }
        public ulong OpticCamera { get; private set; }

        // Static debug copies
        public static ulong FPSCameraPtr { get; private set; }
        public static ulong OpticCameraPtr { get; private set; }
        public static ulong ActiveCameraPtr { get; private set; }

        // Matrix address pointers (via chain: GameObject+0x48→+0x18)
        private ulong _fpsMatrixAddress;
        private ulong _opticMatrixAddress;
        
        // Validation tracking
        private int _consecutiveMatrixFailures;
        private DateTime _lastValidMatrix = DateTime.MinValue;
        private bool _matrixInitialized;

        private bool OpticCameraActive =>
            Memory.ReadValue<bool>(OpticCamera + UnitySDK.UnityOffsets.MonoBehaviour_IsAddedOffset, false);

        public CameraManager()
        {
            _current = this;

            Debug.WriteLine("=== CameraManager Initialization ===");
            Debug.WriteLine($"Unity Base: 0x{Memory.UnityBase:X}");
            Debug.WriteLine($"AllCameras Offset: 0x{UnitySDK.UnityOffsets.AllCameras:X}");

            StartInitThread();
        }
        private void StartInitThread()
        {
            // If a thread is already alive, don't spawn another
            if (_initThread != null && _initThread.IsAlive)
                return;

            _initThreadRunning = true;
            _initThread = new Thread(InitializationLoop)
            {
                IsBackground = true,
                Name = "CameraManager Initialization"
            };
            _initThread.Start();
        }
        /// <summary>
        /// Background thread that keeps retrying camera initialization until successful
        /// </summary>
        private void InitializationLoop()
        {
            int attemptNumber = 0;
            DateTime lastLogTime = DateTime.MinValue;

            // Only do the “old raid cameras” wait when we’re starting from a fresh state
            Debug.WriteLine("[CameraManager] Waiting 15 seconds for old raid cameras to be cleaned up...");
            Thread.Sleep(15000);
            Debug.WriteLine("[CameraManager] Starting camera search for raid...");

            while (_initThreadRunning && !_matrixInitialized)
            {
                try
                {
                    attemptNumber++;
                    bool shouldLog = (DateTime.UtcNow - lastLogTime).TotalSeconds >= 5.0;

                    if (shouldLog)
                    {
                        Debug.WriteLine($"[CameraManager] Initialization attempt #{attemptNumber}...");
                        lastLogTime = DateTime.UtcNow;
                    }

                    if (TryInitializeCameras(shouldLog))
                    {
                        Debug.WriteLine($"[CameraManager] ✓✓✓ Successfully initialized after {attemptNumber} attempts! ✓✓✓");

                        // Let the loop exit; thread will die.
                        // On next raid, we will call StartInitThread() again.
                        _initThreadRunning = false;
                        return;
                    }

                    Thread.Sleep(attemptNumber < 10 ? 500 : 1000);
                }
                catch (Exception ex)
                {
                    if ((DateTime.UtcNow - lastLogTime).TotalSeconds >= 5.0)
                    {
                        Debug.WriteLine($"[CameraManager] Initialization error (will retry): {ex.Message}");
                        lastLogTime = DateTime.UtcNow;
                    }
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>
        /// Try to initialize cameras. Returns true if successful.
        /// </summary>
        private bool TryInitializeCameras(bool verbose)
        {
            try
            {
                // ✅ NEW: don't even try to lock in cameras until the raid / LocalGameWorld is ready
                if (!Memory.InRaid || Memory.LocalPlayer == null)
                {
                    if (verbose)
                    {
                        Debug.WriteLine("[CameraManager] InRaid/LocalPlayer not ready yet - waiting for LocalGameWorld before camera init...");
                    }
                    return false;
                }
                Thread.Sleep(10000); // Give game a moment to spawn cameras

                // Calculate AllCameras address
                var allCamerasAddr = Memory.UnityBase + UnitySDK.UnityOffsets.AllCameras;
                var allCamerasPtr = Memory.ReadPtr(allCamerasAddr, false);

                if (allCamerasPtr == 0 || allCamerasPtr > 0x7FFFFFFFFFFF)
                {
                    if (verbose)
                        Debug.WriteLine($"[CameraManager] AllCameras pointer invalid: 0x{allCamerasPtr:X} (waiting for raid load...)");
                    return false;
                }

                // AllCameras is a List<Camera*>
                var listItemsPtr = Memory.ReadPtr(allCamerasPtr + 0x0, false);
                var count = Memory.ReadValue<int>(allCamerasPtr + 0x8, false);

                if (listItemsPtr == 0 || count <= 0)
                {
                    if (verbose)
                        Debug.WriteLine($"[CameraManager] No cameras in list yet (count: {count}) - waiting for raid to load...");
                    return false;
                }

                if (verbose)
                {
                    Debug.WriteLine($"[CameraManager] Found {count} cameras in list, searching...");
                }

                // Find cameras by name
                var (fps, optic) = FindCamerasByName(listItemsPtr, count, verbose);

                if (fps == 0)
                {
                    if (verbose)
                        Debug.WriteLine($"[CameraManager] FPS Camera not found yet (waiting for raid to spawn cameras...)");
                    return false;
                }

                if (optic == 0)
                {
                    if (verbose)
                        Debug.WriteLine($"[CameraManager] Optic Camera not found yet (waiting...)");
                    return false;
                }

                // Both cameras found! Try to get matrix addresses
                FPSCamera = fps;
                OpticCamera = optic;

                if (verbose)
                {
                    Debug.WriteLine($"[CameraManager] Getting matrix addresses...");
                }

                _fpsMatrixAddress = GetMatrixAddress(FPSCamera);
                _opticMatrixAddress = GetMatrixAddress(OpticCamera);
                
                // ✅ Check if matrix addresses are valid
                if (_fpsMatrixAddress == 0)
                {
                    if (verbose)
                        Debug.WriteLine($"[CameraManager] Failed to get FPS camera matrix address (camera likely stale from previous raid)");
                    return false;
                }
                
                if (_opticMatrixAddress == 0)
                {
                    if (verbose)
                        Debug.WriteLine($"[CameraManager] Failed to get Optic camera matrix address (camera likely stale from previous raid)");
                    return false;
                }

                FPSCameraPtr = fps;
                OpticCameraPtr = optic;
                ActiveCameraPtr = 0;

                if (verbose)
                {
                    Debug.WriteLine($"[CameraManager] ✓ FPS Camera: 0x{FPSCamera:X}");
                    Debug.WriteLine($"[CameraManager]   Matrix Address: 0x{_fpsMatrixAddress:X}");
                    Debug.WriteLine($"[CameraManager] ✓ Optic Camera: 0x{OpticCamera:X}");
                    Debug.WriteLine($"[CameraManager]   Matrix Address: 0x{_opticMatrixAddress:X}");
                }

                // Validate FPS camera matrix
                bool fpsValid = VerifyViewMatrix(_fpsMatrixAddress, verbose ? "FPS" : null);
                
                if (fpsValid)
                {
                    _matrixInitialized = true;
                    _lastValidMatrix = DateTime.UtcNow;
                    
                    Debug.WriteLine("[CameraManager] ✓ FPS Camera validated successfully - READY!");
                    
                    // Check optic (just for logging, not required)
                    bool opticValid = VerifyViewMatrix(_opticMatrixAddress, null);
                    if (!opticValid && verbose)
                    {
                        Debug.WriteLine("[CameraManager] Note: Optic camera not yet valid (normal until you scope in)");
                    }
                    
                    return true;
                }
                else
                {
                    if (verbose)
                        Debug.WriteLine("[CameraManager] FPS camera matrix not yet valid (waiting for game to fully load...)");
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (verbose)
                    Debug.WriteLine($"[CameraManager] TryInitializeCameras error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reset matrix addresses if they appear corrupted
        /// </summary>
        private void ResetMatrixAddresses()
        {
            try
            {
                Debug.WriteLine("[CameraManager] Resetting matrix addresses...");
                
                _fpsMatrixAddress = GetMatrixAddress(FPSCamera);
                _opticMatrixAddress = GetMatrixAddress(OpticCamera);
                
                bool fpsValid = VerifyViewMatrix(_fpsMatrixAddress, "FPS (after reset)");
                
                if (fpsValid)
                {
                    _matrixInitialized = true;
                    _consecutiveMatrixFailures = 0;
                    _lastValidMatrix = DateTime.UtcNow;
                    Debug.WriteLine("[CameraManager] ✓ Matrix reset successful");
                }
                else
                {
                    Debug.WriteLine("[CameraManager] ⚠️ Matrix reset failed - FPS camera still invalid");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraManager] ERROR resetting matrix addresses: {ex}");
            }
        }

        private static ulong GetMatrixAddress(ulong cameraPtr)
        {
            try
            {
                // Camera + 0x50 → GameObject
                var gameObject = Memory.ReadPtr(cameraPtr + UnitySDK.UnityOffsets.Component_GameObjectOffset, false);
                if (gameObject == 0 || gameObject > 0x7FFFFFFFFFFF)
                {
                    Debug.WriteLine($"[GetMatrixAddress] Invalid GameObject: 0x{gameObject:X}");
                    return 0;
                }

                // GameObject + 0x50 → Pointer1
                var ptr1 = Memory.ReadPtr(gameObject + UnitySDK.UnityOffsets.Component_GameObjectOffset, false);
                if (ptr1 == 0 || ptr1 > 0x7FFFFFFFFFFF)
                {
                    Debug.WriteLine($"[GetMatrixAddress] Invalid Ptr1 (GameObject+{UnitySDK.UnityOffsets.Component_GameObjectOffset}): 0x{ptr1:X}");
                    return 0;
                }

                // Pointer1 + 0x18 → matrixAddress
                var matrixAddress = Memory.ReadPtr(ptr1 + 0x18, false);
                if (matrixAddress == 0 || matrixAddress > 0x7FFFFFFFFFFF)
                {
                    Debug.WriteLine($"[GetMatrixAddress] Invalid matrixAddress (Ptr1+0x18): 0x{matrixAddress:X}");
                    return 0;
                }

                return matrixAddress;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetMatrixAddress] Exception: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Validates the current view matrix.
        /// Fixed to not rely on M44 value which changes with rotation.
        /// </summary>
        private bool ValidateCurrentMatrix(ref readonly Matrix4x4 vm)
        {
            // Check for NaN/Infinity (matrix is corrupted)
            if (float.IsNaN(vm.M11) || float.IsInfinity(vm.M11) ||
                float.IsNaN(vm.M22) || float.IsInfinity(vm.M22) ||
                float.IsNaN(vm.M33) || float.IsInfinity(vm.M33) ||
                float.IsNaN(vm.M44) || float.IsInfinity(vm.M44))
            {
                return false;
            }

            // Check for all-zeros (invalid matrix)
            if (vm.M11 == 0f && vm.M22 == 0f && vm.M33 == 0f && vm.M44 == 0f)
            {
                return false;
            }

            // Check translation vector for reasonable world coordinates
            // This catches menu/preview cameras which have unrealistic positions
            float tx = vm.M41;
            float ty = vm.M42;
            float tz = vm.M43;

            if (float.IsNaN(tx) || float.IsInfinity(tx) ||
                float.IsNaN(ty) || float.IsInfinity(ty) ||
                float.IsNaN(tz) || float.IsInfinity(tz))
            {
                return false;
            }

            // Check that translation is within reasonable game world bounds
            if (Math.Abs(tx) > 5000f || Math.Abs(ty) > 5000f || Math.Abs(tz) > 5000f)
            {
                return false;
            }

            // ✅ NEW: reject “origin” matrices while in raid
            if (Memory.InRaid)
            {
                float lenSq = tx * tx + ty * ty + tz * tz;
                if (lenSq < 1.0f)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Verifies a camera matrix address has valid data.
        /// </summary>
        private static bool VerifyViewMatrix(ulong matrixAddress, string name)
        {
            var vm = Memory.ReadValue<Matrix4x4>(matrixAddress + UnitySDK.UnityOffsets.Camera_ViewMatrixOffset, false);
            
            if (!string.IsNullOrEmpty(name))
            {
                Debug.WriteLine($"\n{name} Matrix @ 0x{matrixAddress:X}:");
                Debug.WriteLine($"  M11: {vm.M11:F6}, M22: {vm.M22:F6}, M33: {vm.M33:F6}, M44: {vm.M44:F6}");
                Debug.WriteLine($"  Translation: ({vm.M41:F2}, {vm.M42:F2}, {vm.M43:F2})");
            }
            
            // Check for NaN/Infinity
            if (float.IsNaN(vm.M11) || float.IsInfinity(vm.M11) ||
                float.IsNaN(vm.M22) || float.IsInfinity(vm.M22) ||
                float.IsNaN(vm.M33) || float.IsInfinity(vm.M33) ||
                float.IsNaN(vm.M44) || float.IsInfinity(vm.M44) ||
                float.IsNaN(vm.M41) || float.IsInfinity(vm.M41))
            {
                if (!string.IsNullOrEmpty(name))
                    Debug.WriteLine($"  ✗ Invalid: Contains NaN/Infinity");
                return false;
            }

            // Check for all-zeros
            if (vm.M11 == 0f && vm.M22 == 0f && vm.M33 == 0f && vm.M44 == 0f)
            {
                if (!string.IsNullOrEmpty(name))
                    Debug.WriteLine($"  ✗ Invalid: All zeros");
                return false;
            }

            // Check translation is within reasonable bounds
            if (Math.Abs(vm.M41) > 5000f || Math.Abs(vm.M42) > 5000f || Math.Abs(vm.M43) > 5000f)
            {
                if (!string.IsNullOrEmpty(name))
                    Debug.WriteLine($"  ✗ Invalid: Translation out of bounds");
                return false;
            }
            // ✅ NEW: reject origin-ish translations when we *are* in raid
            // Menu/preview cameras often sit at (0,0,0)
            if (Memory.InRaid)
            {
                float tx = vm.M41;
                float ty = vm.M42;
                float tz = vm.M43;

                float lenSq = tx * tx + ty * ty + tz * tz;
                if (lenSq < 1.0f) // ~distance < 1m from origin
                {
                    if (!string.IsNullOrEmpty(name))
                        Debug.WriteLine($"  ✗ Invalid: Translation too close to origin while in raid");
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(name))
                Debug.WriteLine($"  ✓ Valid: Matrix looks good");

            return true;
        }

        private static (ulong fpsCamera, ulong opticCamera) FindCamerasByName(ulong listItemsPtr, int count, bool verbose)
        {
            ulong fpsCamera = 0;
            ulong opticCamera = 0;

            if (verbose)
            {
                Debug.WriteLine($"[CameraManager] Scanning {count} cameras for FPS/Optic...");
            }

            for (int i = 0; i < Math.Min(count, 100); i++)
            {
                try
                {
                    // Each item in the array is a pointer (8 bytes)
                    ulong cameraEntryAddr = listItemsPtr + (uint)(i * 0x8);
                    var cameraPtr = Memory.ReadPtr(cameraEntryAddr, false);
                    
                    if (cameraPtr == 0 || cameraPtr > 0x7FFFFFFFFFFF)
                        continue;

                    // Camera+0x50 -> GameObject
                    var gameObjectPtr = Memory.ReadPtr(cameraPtr + UnitySDK.UnityOffsets.Component_GameObjectOffset, false);
                    if (gameObjectPtr == 0 || gameObjectPtr > 0x7FFFFFFFFFFF)
                        continue;

                    // GameObject+0x78 -> Name string pointer
                    var namePtr = Memory.ReadPtr(gameObjectPtr + UnitySDK.UnityOffsets.GameObject_NameOffset, false);
                    if (namePtr == 0 || namePtr > 0x7FFFFFFFFFFF)
                        continue;

                    // Read the name string
                    var name = Memory.ReadUtf8String(namePtr, 64, false);
                    if (string.IsNullOrEmpty(name) || name.Length < 3)
                        continue;

                    if (verbose)
                    {
                        Debug.WriteLine($"  [{i:D2}] Camera: '{name}' @ 0x{cameraPtr:X}");
                    }

                    // Check for FPS Camera
                    bool isFPS = name.Contains("FPS", StringComparison.OrdinalIgnoreCase) &&
                                name.Contains("Camera", StringComparison.OrdinalIgnoreCase);
                    
                    // Check for Optic Camera  
                    bool isOptic = (name.Contains("Optic", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("BaseOptic", StringComparison.OrdinalIgnoreCase)) &&
                                  name.Contains("Camera", StringComparison.OrdinalIgnoreCase);

                    if (isFPS)
                    {
                        fpsCamera = cameraPtr;
                        if (verbose)
                            Debug.WriteLine($"       ✓✓✓ MATCHED AS FPS CAMERA ✓✓✓");
                    }
                    
                    if (isOptic)
                    {
                        opticCamera = cameraPtr;
                        if (verbose)
                            Debug.WriteLine($"       ✓✓✓ MATCHED AS OPTIC CAMERA ✓✓✓");
                    }

                    if (fpsCamera != 0 && opticCamera != 0)
                    {
                        if (verbose)
                            Debug.WriteLine($"[CameraManager] Both cameras found! Stopping search at index {i}.");
                        break;
                    }
                }
                catch
                {
                    // Silently skip bad entries during retry loop
                }
            }

            if (verbose)
            {
                Debug.WriteLine($"[CameraManager] Search Results:");
                Debug.WriteLine($"  FPS Camera:   {(fpsCamera != 0 ? $"✓ Found @ 0x{fpsCamera:X}" : "✗ NOT FOUND")}");
                Debug.WriteLine($"  Optic Camera: {(opticCamera != 0 ? $"✓ Found @ 0x{opticCamera:X}" : "✗ NOT FOUND")}");
            }

            return (fpsCamera, opticCamera);
        }

        static CameraManager()
        {
            MemDMA.ProcessStarting += MemDMA_ProcessStarting;
            MemDMA.ProcessStopped += MemDMA_ProcessStopped;
            MemDMA.RaidStopped += MemDMA_RaidStopped;
        }
        private static void MemDMA_RaidStopped(object sender, EventArgs e)
        {
            Debug.WriteLine("[CameraManager] Raid stopped - clearing camera state");

            // Reset static view matrix so ESP doesn't use a stale one
            var identity = Matrix4x4.Identity;
            _viewMatrix.Update(ref identity);
            EspRunning = false;

            FPSCameraPtr = 0;
            OpticCameraPtr = 0;
            ActiveCameraPtr = 0;
            _zoomLevel = 1.0f;
            _fov = 0f;
            _aspect = 0f;

            if (_current is { } inst)
            {
                inst._matrixInitialized = false;
                inst._consecutiveMatrixFailures = 0;
                inst._lastValidMatrix = DateTime.MinValue;
                inst._fpsMatrixAddress = 0;
                inst._opticMatrixAddress = 0;
                inst.FPSCamera = 0;
                inst.OpticCamera = 0;

                // ❌ REMOVE this line when MemDMA creates a fresh CameraManager each raid
                // inst.StartInitThread();
            }
        }


        private static void MemDMA_ProcessStarting(object sender, EventArgs e) { }
        private static void MemDMA_ProcessStopped(object sender, EventArgs e) { }

        private bool CheckIfScoped(LocalPlayer localPlayer)
        {
            try
            {
                if (localPlayer is null || !OpticCameraActive)
                    return false;

                var opticsPtr = Memory.ReadPtr(localPlayer.PWA + Offsets.ProceduralWeaponAnimation._optics);
                using var optics = MonoList<VmmPointer>.Create(opticsPtr, true);

                if (optics.Count > 0)
                {
                    var pSightComponent = Memory.ReadPtr(optics[0] + Offsets.SightNBone.Mod);
                    var sightComponent = Memory.ReadValue<SightComponent>(pSightComponent);

                    if (sightComponent.ScopeZoomValue != 0f)
                        return sightComponent.ScopeZoomValue > 0f;

                    return sightComponent.GetZoomLevel() > 1f;
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CheckIfScoped() ERROR: {ex}");
                return false;
            }
        }

        public void OnRealtimeLoop(VmmScatter scatter, LocalPlayer localPlayer)
        {
            try
            {
                // Don't do camera things if we aren't in a raid or local player isn't ready
                if (!Memory.InRaid || localPlayer == null)
                    return;

                // Also don't touch matrices until init succeeded
                if (!_matrixInitialized || FPSCamera == 0 || _fpsMatrixAddress == 0)
                    return;

                IsADS = localPlayer?.CheckIfADS() ?? false;
                IsScoped = IsADS && CheckIfScoped(localPlayer);

                ulong activeMatrixAddress = (IsADS && IsScoped) ? _opticMatrixAddress : _fpsMatrixAddress;
                ulong activeCamera = (IsADS && IsScoped) ? OpticCamera : FPSCamera;

                if (activeMatrixAddress == 0 || activeCamera == 0)
                    return;

                ActiveCameraPtr = activeCamera;

                // Prepare reads...
                scatter.PrepareReadValue<Matrix4x4>(activeMatrixAddress + UnitySDK.UnityOffsets.Camera_ViewMatrixOffset);
                scatter.PrepareReadValue<float>(FPSCamera + UnitySDK.UnityOffsets.Camera_FOVOffset);
                scatter.PrepareReadValue<float>(FPSCamera + UnitySDK.UnityOffsets.Camera_AspectRatioOffset);
                scatter.PrepareReadValue<float>(activeCamera + UnitySDK.UnityOffsets.Camera_ZoomLevelOffset);

                scatter.Completed += (sender, s) =>
                {
                    try
                    {
                        if (s.ReadValue<Matrix4x4>(activeMatrixAddress + UnitySDK.UnityOffsets.Camera_ViewMatrixOffset, out var vm))
                        {
                            if (ValidateCurrentMatrix(in vm))
                            {
                                _viewMatrix.Update(ref vm);
                                _consecutiveMatrixFailures = 0;
                                _lastValidMatrix = DateTime.UtcNow;

                                if (!_matrixInitialized)
                                {
                                    _matrixInitialized = true;
                                    Debug.WriteLine("[CameraManager] ✓ Matrix validated for first time in runtime");
                                }
                            }
                            else
                            {
                                _consecutiveMatrixFailures++;

                                if (_consecutiveMatrixFailures % 10 == 0)
                                {
                                    Debug.WriteLine($"[CameraManager] Invalid matrix - {_consecutiveMatrixFailures} consecutive failures");
                                }
                            }
                        }
                        else
                        {
                            _consecutiveMatrixFailures++;
                        }

                        // Optional: if matrices are broken for a long time mid-raid, force re-init
                        if (_consecutiveMatrixFailures >= 120)
                        {
                            Debug.WriteLine("[CameraManager] Too many invalid matrices, invalidating and restarting camera initialization");
                            _matrixInitialized = false;
                            _consecutiveMatrixFailures = 0;
                            FPSCamera = 0;
                            OpticCamera = 0;
                            _fpsMatrixAddress = 0;
                            _opticMatrixAddress = 0;
                            FPSCameraPtr = OpticCameraPtr = ActiveCameraPtr = 0;
                            StartInitThread();
                            return;
                        }

                        if (s.ReadValue<float>(FPSCamera + UnitySDK.UnityOffsets.Camera_FOVOffset, out var fov))
                            _fov = fov;

                        if (s.ReadValue<float>(FPSCamera + UnitySDK.UnityOffsets.Camera_AspectRatioOffset, out var aspect))
                            _aspect = aspect;

                        if (s.ReadValue<float>(activeCamera + UnitySDK.UnityOffsets.Camera_ZoomLevelOffset, out var zoom))
                            _zoomLevel = zoom;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ERROR in CameraManager scatter callback: {ex}");
                    }
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in CameraManager OnRealtimeLoop: {ex}");
            }
        }


        // Add field at top of class
        private static float _zoomLevel = 1.0f;

        // Add public property
        public static float ZoomLevel => _zoomLevel;

        /// <summary>
        /// Check if camera manager is ready to use (matrices initialized with valid data)
        /// </summary>
        public bool IsInitialized => _matrixInitialized && 
                                     (DateTime.UtcNow - _lastValidMatrix).TotalSeconds < 5.0;

        #region SightComponent Structures

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        private readonly struct SightComponent
        {
            [FieldOffset((int)Offsets.SightComponent._template)]
            private readonly ulong pSightInterface;

            [FieldOffset((int)Offsets.SightComponent.ScopesSelectedModes)]
            private readonly ulong pScopeSelectedModes;

            [FieldOffset((int)Offsets.SightComponent.SelectedScope)]
            private readonly int SelectedScope;

            [FieldOffset((int)Offsets.SightComponent.ScopeZoomValue)]
            public readonly float ScopeZoomValue;

            public readonly float GetZoomLevel()
            {
                try
                {
                    using var zoomArray = SightInterface.Zooms;
                    if (SelectedScope >= zoomArray.Count || SelectedScope is < 0 or > 10)
                        return -1.0f;

                    using var selectedScopeModes = MonoArray<int>.Create(pScopeSelectedModes, false);
                    int selectedScopeMode = SelectedScope >= selectedScopeModes.Count ? 0 : selectedScopeModes[SelectedScope];
                    ulong zoomAddr = zoomArray[SelectedScope] + MonoArray<float>.ArrBaseOffset + (uint)selectedScopeMode * 0x4;

                    float zoomLevel = Memory.ReadValue<float>(zoomAddr, false);
                    if (zoomLevel.IsNormalOrZero() && zoomLevel is >= 0f and < 100f)
                        return zoomLevel;

                    return -1.0f;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ERROR in GetZoomLevel: {ex}");
                    return -1.0f;
                }
            }

            public readonly SightInterface SightInterface => Memory.ReadValue<SightInterface>(pSightInterface);
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        private readonly struct SightInterface
        {
            [FieldOffset((int)Offsets.SightInterface.Zooms)]
            private readonly ulong pZooms;

            public readonly MonoArray<ulong> Zooms => MonoArray<ulong>.Create(pZooms, true);
        }

        #endregion

        #region Static Interfaces

        private const int VIEWPORT_TOLERANCE = 800;
        private static readonly Lock _viewportSync = new();

        public static bool EspRunning { get; set; }
        public static Rectangle Viewport { get; private set; }
        public static SKPoint ViewportCenter => new SKPoint(Viewport.Width / 2f, Viewport.Height / 2f);
        public static bool IsScoped { get; private set; }
        public static bool IsADS { get; private set; }

        private static float _fov;
        private static float _aspect;
        private static readonly ViewMatrix _viewMatrix = new();

        public static void UpdateViewportRes()
        {
            lock (_viewportSync)
            {
                var width = (int)App.Config.ESP.Resolution.Width;
                var height = (int)App.Config.ESP.Resolution.Height;
                
                // Fallback to 1080p if invalid
                if (width <= 0 || height <= 0)
                {
                    width = 1920;
                    height = 1080;
                }
                
                Viewport = new Rectangle(0, 0, width, height);
                Debug.WriteLine($"[CameraManager] Viewport updated to {width}x{height}");
            }
        }

        public static bool WorldToScreen(
            ref readonly Vector3 worldPos,
            out SKPoint scrPos,
            bool onScreenCheck = false,
            bool useTolerance = false)
        {
            try
            {
                float w = Vector3.Dot(_viewMatrix.Translation, worldPos) + _viewMatrix.M44;

                if (w < 0.098f)
                {
                    scrPos = default;
                    return false;
                }

                float x = Vector3.Dot(_viewMatrix.Right, worldPos) + _viewMatrix.M14;
                float y = Vector3.Dot(_viewMatrix.Up, worldPos) + _viewMatrix.M24;

                // Only use FOV-based calculation when scoped
                if (IsScoped)
                {
                    float angleRadHalf = (MathF.PI / 180f) * _fov * 0.5f;
                    float angleCtg = MathF.Cos(angleRadHalf) / MathF.Sin(angleRadHalf);

                    x /= angleCtg * _aspect * 0.5f;
                    y /= angleCtg * 0.5f;
                }

                var center = ViewportCenter;
                scrPos = new()
                {
                    X = center.X * (1f + x / w),
                    Y = center.Y * (1f - y / w)
                };

                if (onScreenCheck)
                {
                    int left = useTolerance ? Viewport.Left - VIEWPORT_TOLERANCE : Viewport.Left;
                    int right = useTolerance ? Viewport.Right + VIEWPORT_TOLERANCE : Viewport.Right;
                    int top = useTolerance ? Viewport.Top - VIEWPORT_TOLERANCE : Viewport.Top;
                    int bottom = useTolerance ? Viewport.Bottom + VIEWPORT_TOLERANCE : Viewport.Bottom;

                    if (scrPos.X < left || scrPos.X > right || scrPos.Y < top || scrPos.Y > bottom)
                    {
                        scrPos = default;
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in WorldToScreen: {ex}");
                scrPos = default;
                return false;
            }
        }

        public static CameraDebugSnapshot GetDebugSnapshot()
        {
            return new CameraDebugSnapshot
            {
                EspRunning = EspRunning,
                IsADS = IsADS,
                IsScoped = IsScoped,
                FPSCamera = FPSCameraPtr,
                OpticCamera = OpticCameraPtr,
                ActiveCamera = ActiveCameraPtr,
                Fov = _fov,
                Aspect = _aspect,
                M14 = _viewMatrix.M14,
                M24 = _viewMatrix.M24,
                M44 = _viewMatrix.M44,
                RightX = _viewMatrix.Right.X,
                RightY = _viewMatrix.Right.Y,
                RightZ = _viewMatrix.Right.Z,
                UpX = _viewMatrix.Up.X,
                UpY = _viewMatrix.Up.Y,
                UpZ = _viewMatrix.Up.Z,
                TransX = _viewMatrix.Translation.X,
                TransY = _viewMatrix.Translation.Y,
                TransZ = _viewMatrix.Translation.Z,
                ViewportW = Viewport.Width,
                ViewportH = Viewport.Height
            };
        }

        public readonly struct CameraDebugSnapshot
        {
            public bool EspRunning { get; init; }
            public bool IsADS { get; init; }
            public bool IsScoped { get; init; }
            public ulong FPSCamera { get; init; }
            public ulong OpticCamera { get; init; }
            public ulong ActiveCamera { get; init; }
            public float Fov { get; init; }
            public float Aspect { get; init; }
            public float M14 { get; init; }
            public float M24 { get; init; }
            public float M44 { get; init; }
            public float RightX { get; init; }
            public float RightY { get; init; }
            public float RightZ { get; init; }
            public float UpX { get; init; }
            public float UpY { get; init; }
            public float UpZ { get; init; }
            public float TransX { get; init; }
            public float TransY { get; init; }
            public float TransZ { get; init; }
            public int ViewportW { get; init; }
            public int ViewportH { get; init; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetFovMagnitude(SKPoint point)
        {
            return Vector2.Distance(ViewportCenter.AsVector2(), point.AsVector2());
        }
        #endregion
    }
}