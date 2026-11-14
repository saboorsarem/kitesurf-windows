// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Windows;

namespace Kitesurf.Windows.Core;

/// <summary>
/// Converts a point in the page surface's layout space into the engine's viewport coordinates.
/// The engine renders exactly the pixel box the client asks for, so the ratio comes from the
/// last frame rather than from a DPI assumption.
/// </summary>
public static class ViewportMapping
{
    public static (double X, double Y) ToPage(Point position, double surfaceWidth, double surfaceHeight, int frameWidth, int frameHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || frameWidth <= 0 || frameHeight <= 0) return (0, 0);

        var scaleX = frameWidth / surfaceWidth;
        var scaleY = frameHeight / surfaceHeight;

        var x = Math.Clamp(position.X * scaleX, 0, frameWidth);
        var y = Math.Clamp(position.Y * scaleY, 0, frameHeight);
        return (x, y);
    }
}
