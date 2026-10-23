using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace RDS_Results;

sealed class CameraPreview : IDisposable
{
    readonly Image _surface;
    readonly Action<string> _log;
    readonly LibVLCSharp.Shared.MediaPlayer.LibVLCVideoFormatCb _format;
    readonly LibVLCSharp.Shared.MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    readonly LibVLCSharp.Shared.MediaPlayer.LibVLCVideoLockCb _lock;
    readonly LibVLCSharp.Shared.MediaPlayer.LibVLCVideoDisplayCb _display;
    readonly object _frameGate = new();

    LibVLC? _libVlc;
    LibVLCSharp.Shared.MediaPlayer? _player;
    WriteableBitmap? _bitmap;
    IntPtr _buffer;
    int _bufferSize;
    int _width;
    int _height;
    int _pitch;
    byte[]? _latest;
    int _pending;
    bool _disposed;

    public CameraPreview(Image surface, Action<string> log)
    {
        _surface = surface;
        _log = log;
        _format = OnFormat;
        _cleanup = OnCleanup;
        _lock = OnLock;
        _display = OnDisplay;
    }

    public bool Start()
    {
        if (_player != null)
            return true;
        try
        {
            Core.Initialize();
            _libVlc = new LibVLC(
                "--avcodec-hw=none",
                "--network-caching=1000",
                "--live-caching=300",
                "--rtsp-tcp",
                "--no-audio",
                "--no-osd",
                "--no-video-title-show",
                "--swscale-mode=2",
                "--avcodec-skiploopfilter=none",
                "--avcodec-skip-frame=0",
                "--avcodec-skip-idct=0",
                "--quiet");
            _player = new LibVLCSharp.Shared.MediaPlayer(_libVlc)
            {
                EnableHardwareDecoding = false
            };
            _player.SetVideoFormatCallbacks(_format, _cleanup);
            _player.SetVideoCallbacks(_lock, null, _display);
            _player.EncounteredError += (_, _) =>
                _surface.Dispatcher.BeginInvoke(() => _log("Camera stream failed. Check the RTSP URL."));
            _player.Playing += (_, _) => _surface.Dispatcher.BeginInvoke(DescribeVideo);
            return true;
        }
        catch (Exception ex)
        {
            _log("Camera player failed to start. " + ex.Message);
            Dispose();
            return false;
        }
    }

    public void Play(string url)
    {
        if (_player == null || _libVlc == null)
            return;
        using var media = new Media(_libVlc, url, FromType.FromLocation);
        media.AddOption(":rtsp-tcp");
        media.AddOption(":network-caching=1000");
        media.AddOption(":no-audio");
        media.AddOption(":avcodec-hw=none");
        media.AddOption(":clock-jitter=0");
        media.AddOption(":clock-synchro=0");
        _player.Play(media);
    }

    public void StopPlayback()
    {
        try
        {
            _player?.Stop();
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopPlayback();
        try { _player?.Dispose(); } catch (Exception) { }
        try { _libVlc?.Dispose(); } catch (Exception) { }
        _player = null;
        _libVlc = null;
        FreeBuffer();
        _surface.Dispatcher.BeginInvoke(() => _surface.Source = null);
    }

    void DescribeVideo()
    {
        var media = _player?.Media;
        if (media == null)
            return;
        foreach (var track in media.Tracks)
        {
            if (track.TrackType != TrackType.Video)
                continue;
            var video = track.Data.Video;
            var codec = media.CodecDescription(TrackType.Video, track.Codec);
            if (string.IsNullOrWhiteSpace(codec))
                codec = FourCc(track.Codec);
            _log($"Camera stream {video.Width}x{video.Height} {codec}.");
            return;
        }
    }

    static string FourCc(uint codec)
    {
        var bytes = BitConverter.GetBytes(codec);
        return System.Text.Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
    }

    uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        var rv32 = "RV32"u8.ToArray();
        Marshal.Copy(rv32, 0, chroma, 4);
        width = Align(width, 2);
        height = Align(height, 2);
        pitches = Align(width * 4, 32);
        lines = height;
        lock (_frameGate)
        {
            _width = (int)width;
            _height = (int)height;
            _pitch = (int)pitches;
            AllocateBuffer(_pitch * _height);
        }

        return 1;
    }

    void OnCleanup(ref IntPtr opaque) => FreeBuffer();

    IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_buffer == IntPtr.Zero || _bufferSize == 0)
            return;
        lock (_frameGate)
        {
            if (_latest == null || _latest.Length < _bufferSize)
                _latest = new byte[_bufferSize];
            Marshal.Copy(_buffer, _latest, 0, _bufferSize);
        }

        if (Interlocked.Exchange(ref _pending, 1) == 1)
            return;
        _surface.Dispatcher.BeginInvoke(PushFrame, DispatcherPriority.Render);
    }

    void PushFrame()
    {
        try
        {
            if (_disposed)
                return;
            byte[]? data;
            int width;
            int height;
            int pitch;
            lock (_frameGate)
            {
                data = _latest;
                width = _width;
                height = _height;
                pitch = _pitch;
            }

            if (data == null || width < 2 || height < 2)
                return;
            if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                _surface.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), data, pitch, 0);
        }
        finally
        {
            Interlocked.Exchange(ref _pending, 0);
        }
    }

    void AllocateBuffer(int size)
    {
        if (size <= 0)
            return;
        if (_buffer != IntPtr.Zero && _bufferSize == size)
            return;
        FreeBufferUnlocked();
        _buffer = Marshal.AllocHGlobal(size);
        _bufferSize = size;
    }

    void FreeBuffer()
    {
        lock (_frameGate)
            FreeBufferUnlocked();
    }

    void FreeBufferUnlocked()
    {
        if (_buffer == IntPtr.Zero)
            return;
        Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
        _bufferSize = 0;
    }

    static uint Align(uint value, uint multiple) =>
        multiple <= 1 ? value : (value + multiple - 1) / multiple * multiple;
}
