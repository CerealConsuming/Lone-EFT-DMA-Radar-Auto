/*
 * Lone EFT DMA Radar - ESP Widget
 * MIT License - Copyright (c) 2025 Lone DMA
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using LoneEftDmaRadar.DMA;
using LoneEftDmaRadar.Tarkov.GameWorld.Camera;
using LoneEftDmaRadar.Tarkov.GameWorld.Player;
using LoneEftDmaRadar.Tarkov.GameWorld.Player.Helpers;
using LoneEftDmaRadar.Tarkov.Unity.Structures;
using SkiaSharp;
using SkiaSharp.Views.WPF;

namespace LoneEftDmaRadar.UI.Skia
{
    public sealed class EspWidget : IDisposable
    {
        private readonly SKGLElement _skElement;

        public EspWidget(SKGLElement skElement)
        {
            _skElement = skElement;
            _skElement.PaintSurface += OnPaintSurface;
        }

        private static LocalPlayer _LocalPlayer => Memory.LocalPlayer;
        private static IReadOnlyCollection<AbstractPlayer> AllPlayers => Memory.Players;
        private static bool InRaid => Memory.InRaid;

        private void OnPaintSurface(object sender, SKPaintGLSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            if (!InRaid || !CameraManager.EspRunning)
                return;

            if (_LocalPlayer is not LocalPlayer localPlayer)
                return;

            // ? Check if camera manager is initialized with valid matrices
            var cameraManager = MemDMA.CameraManager;
            if (cameraManager == null || !cameraManager.IsInitialized)
            {
                // Only log occasionally to avoid spam
                if (DateTime.UtcNow.Second % 5 == 0)
                {
                    Debug.WriteLine("[ESP] Waiting for camera initialization...");
                }
                return;
            }

            try
            {
                DrawPlayers(canvas, localPlayer);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ESP Render Error: {ex}");
            }
        }

        private void DrawPlayers(SKCanvas canvas, LocalPlayer localPlayer)
        {
            var players = AllPlayers?
                .Where(p => p.IsActive && p.IsAlive && p != localPlayer)
                .ToList();

            if (players == null || players.Count == 0)
                return;

            // Hard cap to avoid worst-case spam in crazy lobbies
            const int MAX_PLAYERS_TO_DRAW = 64;

            // Simple distance cull ¨C no need to draw people 600m away on ESP
            const float MAX_ESP_DIST = 400.0f;
            float maxDistSq = MAX_ESP_DIST * MAX_ESP_DIST;

            int skeletonsDrawn = 0;
            int skeletonsAttempted = 0;
            int skeletonsNotInitialized = 0;

            foreach (var player in players)
            {
                try
                {
                    // Distance cull using last known positions (cheap)
                    var delta = player.Position - localPlayer.Position;
                    if (delta.LengthSquared() > maxDistSq)
                        continue;

                    skeletonsAttempted++;

                    var skeleton = player.Skeleton;
                    if (skeleton == null)
                        continue;

                    if (!skeleton.IsInitialized)
                    {
                        skeletonsNotInitialized++;
                        continue;
                    }

                    if (DrawPlayer(canvas, player, localPlayer))
                    {
                        skeletonsDrawn++;
                        if (skeletonsDrawn >= MAX_PLAYERS_TO_DRAW)
                            break; // hard cap for safety
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error drawing player '{player.Name}': {ex}");
                }
            }

            if (skeletonsAttempted > 0 && skeletonsDrawn == 0)
            {
                if (skeletonsNotInitialized == skeletonsAttempted)
                {
                    if (DateTime.UtcNow.Second % 5 == 0)
                        Debug.WriteLine($"[ESP] {skeletonsAttempted} players, skeletons not initialized yet (waiting for realtime loop).");
                }
                else
                {
                    Debug.WriteLine($"[ESP] {skeletonsAttempted} attempted, {skeletonsNotInitialized} not initialized, 0 drawn.");
                }
            }
        }

        /// <summary>
        /// Draw a single player. Returns true if something was actually drawn.
        /// </summary>
        private bool DrawPlayer(SKCanvas canvas, AbstractPlayer player, LocalPlayer localPlayer)
        {
            var skeleton = player.Skeleton;
            if (skeleton == null || !skeleton.IsInitialized)
                return false;
        
            // Safely get head and pelvis
            if (!skeleton.Bones.TryGetValue(Bones.HumanHead, out var head) ||
                !skeleton.Bones.TryGetValue(Bones.HumanPelvis, out var pelvis))
            {
                return false;
            }
        
            var headPos   = head.Position;
            var pelvisPos = pelvis.Position;
        
            if (!IsFinite(headPos) || !IsFinite(pelvisPos))
                return false;
        
            // World-space sanity check (rough height between head & pelvis)
            float worldHeight = Math.Abs(pelvisPos.Y - headPos.Y);
            if (worldHeight <= 0.01f || worldHeight > 4.0f) // ~0¨C4m allowed
                return false;
        
            // World->Screen
            if (!CameraManager.WorldToScreen(in headPos, out var headScreen, true))
                return false;
        
            if (!CameraManager.WorldToScreen(in pelvisPos, out var pelvisScreen, true))
                return false;
        
            if (!IsFinite(headScreen) || !IsFinite(pelvisScreen))
                return false;
        
            // Get screen bounds
            var bounds = canvas.LocalClipBounds;
            float scrW = bounds.Width;
            float scrH = bounds.Height;
        
            // ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
            // HARD CLAMP: only draw if both points are on-screen
            // (with a tiny margin to avoid flicker on the edge)
            // ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
            const float MARGIN = 5f;
        
            bool headOnScreen =
                headScreen.X >= -MARGIN && headScreen.X <= scrW + MARGIN &&
                headScreen.Y >= -MARGIN && headScreen.Y <= scrH + MARGIN;
        
            bool pelvisOnScreen =
                pelvisScreen.X >= -MARGIN && pelvisScreen.X <= scrW + MARGIN &&
                pelvisScreen.Y >= -MARGIN && pelvisScreen.Y <= scrH + MARGIN;
        
            if (!headOnScreen || !pelvisOnScreen)
                return false;
        
            // Length of the main segment (head -> pelvis)
            float dx      = pelvisScreen.X - headScreen.X;
            float dy      = pelvisScreen.Y - headScreen.Y;
            float lineLen = MathF.Sqrt(dx * dx + dy * dy);
        
            if (lineLen <= 0.5f)
                return false;
        
            // Also clamp against screen size: if it's taller than screen, skip
            float height = Math.Abs(pelvisScreen.Y - headScreen.Y);
            float width  = height * 0.5f;
        
            if (height > scrH || width > scrW)
                return false;
        
            var boxRect = new SKRect(
                headScreen.X - width / 2f,
                headScreen.Y,
                headScreen.X + width / 2f,
                pelvisScreen.Y
            );
        
            var paint = GetPaint(player);
        
            // Box
            canvas.DrawRect(boxRect, paint);
        
            // Head dot
            if (App.Config.ESP.ShowHeadDot)
            {
                canvas.DrawCircle(headScreen.X, headScreen.Y, 3f, paint);
            }
        
            // Name
            if (App.Config.ESP.ShowNames)
            {
                var namePos = new SKPoint(boxRect.MidX, boxRect.Top - 5);
                canvas.DrawText(player.Name, namePos, SKTextAlign.Center, SKFonts.UIRegular, paint);
            }
        
            // Distance
            if (App.Config.ESP.ShowDistance)
            {
                float distance = Vector3.Distance(localPlayer.Position, player.Position);
                var distText   = $"{distance:F0}m";
                var distPos    = new SKPoint(boxRect.MidX, boxRect.Bottom + 15);
                canvas.DrawText(distText, distPos, SKTextAlign.Center, SKFonts.UIRegular, paint);
            }
        
            return true;
        }



        private static SKPaint GetPaint(AbstractPlayer player)
        {
            return player.Type switch
            {
                PlayerType.Teammate      => SKPaints.PaintTeammate,
                PlayerType.PMC           => SKPaints.PaintPMC,
                PlayerType.AIScav        => SKPaints.PaintScav,
                PlayerType.AIRaider      => SKPaints.PaintRaider,
                PlayerType.AIBoss        => SKPaints.PaintBoss,
                PlayerType.PScav         => SKPaints.PaintPScav,
                PlayerType.SpecialPlayer => SKPaints.PaintAimviewWidgetWatchlist,
                _                        => SKPaints.PaintPMC
            };
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.X) && !float.IsInfinity(v.X) &&
                   !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) &&
                   !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
        }

        private static bool IsFinite(SKPoint p)
        {
            return !float.IsNaN(p.X) && !float.IsInfinity(p.X) &&
                   !float.IsNaN(p.Y) && !float.IsInfinity(p.Y);
        }

        public void Dispose()
        {
            _skElement.PaintSurface -= OnPaintSurface;
        }
    }
}