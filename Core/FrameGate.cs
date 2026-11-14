// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

namespace Kitesurf.Windows.Core;

/// <summary>
/// Gates callbacks from engine sessions. Each session opens a token when it is attached; opening a
/// new one retires every older token, so a frame decoded just before a socket drop cannot paint over
/// the session that replaced it.
/// </summary>
public sealed class FrameGate
{
    private readonly object _lock = new();
    private int _current;

    public int Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public int Open()
    {
        lock (_lock) return ++_current;
    }

    public bool Accepts(int token)
    {
        lock (_lock) return token != 0 && token == _current;
    }
}
