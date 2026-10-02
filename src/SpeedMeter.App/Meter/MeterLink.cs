using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Text;
using SpeedMeter.Core;

namespace SpeedMeter.App.Meter;

/// <summary>What the meter publishes once a second for the dashboard to read.</summary>
public sealed record LiveState(double Down, double Up, bool Connected, string Source, int MinutesWritten, int SettingsVersion, string? Problem);

/// <summary>
/// The link between the two processes of the app: the METER (always
/// running, in the tray) and the DASHBOARD (only while its window is open).
/// </summary>
/// <remarks>
/// <para>Why two processes: the dashboard's framework, WinUI, cannot be
/// unloaded once loaded, and costs over a hundred megabytes. In its own
/// process it is gone the moment the window closes, and the meter that runs
/// all day stays at the size of the C# meter it replaces.</para>
/// <para>Meter to dashboard is a small shared-memory block, rewritten every
/// second: the live reading, a count of minutes written to the history (so
/// the dashboard knows when to refresh) and a settings version (so it
/// re-reads settings.ini after a change made from the tray). A sequence
/// number, odd while a write is under way, lets the reader skip a torn read.</para>
/// <para>Dashboard to meter is a named pipe taking one line per command:
/// reload settings.ini, show a notification, exit.</para>
/// </remarks>
public static class MeterLink
{
    private const int Size = 1024;
    private const int SourceChars = 64;
    private const int ProblemChars = 200;

    /// <summary>Per settings folder, like the single instance: a demo copy has its own link.</summary>
    private static string Name(string suffix) => "InternetSpeedMeterNative." + Program.InstanceSuffix + "." + suffix;

    /* ------------------------------------------------------------ Writer */

    /// <summary>The meter's side of the shared block.</summary>
    public sealed class Publisher : IDisposable
    {
        private readonly MemoryMappedFile _file = MemoryMappedFile.CreateOrOpen(Name("live"), Size);
        private readonly MemoryMappedViewAccessor _view;
        private long _sequence;

        public Publisher() => _view = _file.CreateViewAccessor(0, Size);

        public void Write(LiveState s)
        {
            _view.Write(0, ++_sequence);
            _view.Write(8, s.Down);
            _view.Write(16, s.Up);
            _view.Write(24, s.Connected ? 1 : 0);
            _view.Write(28, s.MinutesWritten);
            _view.Write(32, s.SettingsVersion);
            WriteString(40, s.Source, SourceChars);
            WriteString(40 + 4 + SourceChars * 2, s.Problem ?? "", ProblemChars);
            _view.Write(0, ++_sequence);
        }

        private void WriteString(long at, string value, int max)
        {
            var text = value.Length > max ? value[..max] : value;
            _view.Write(at, text.Length);
            var chars = text.ToCharArray();
            _view.WriteArray(at + 4, chars, 0, chars.Length);
        }

        public void Dispose()
        {
            _view.Dispose();
            _file.Dispose();
        }
    }

    /* ------------------------------------------------------------ Reader */

    /// <summary>The latest state, or null when no meter is running.</summary>
    public static LiveState? Read()
    {
        try
        {
            using var file = MemoryMappedFile.OpenExisting(Name("live"), MemoryMappedFileRights.Read);
            using var view = file.CreateViewAccessor(0, Size, MemoryMappedFileAccess.Read);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var before = view.ReadInt64(0);
                if (before % 2 == 1) continue;
                var state = new LiveState(
                    view.ReadDouble(8), view.ReadDouble(16), view.ReadInt32(24) == 1,
                    ReadString(view, 40, SourceChars), view.ReadInt32(28), view.ReadInt32(32),
                    ReadString(view, 40 + 4 + SourceChars * 2, ProblemChars) is { Length: > 0 } p ? p : null);
                if (view.ReadInt64(0) == before) return state;
            }
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadString(MemoryMappedViewAccessor view, long at, int max)
    {
        var length = Math.Clamp(view.ReadInt32(at), 0, max);
        var chars = new char[length];
        view.ReadArray(at + 4, chars, 0, length);
        return new string(chars);
    }

    /* ---------------------------------------------------------- Commands */

    /// <summary>The meter's command pipe: one line per connection, handed to <paramref name="handle"/> on a worker thread.</summary>
    public static void Serve(Action<string[]> handle, CancellationToken stop) => new Thread(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Name("commands"), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipe.WaitForConnectionAsync(stop).GetAwaiter().GetResult();
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                if (reader.ReadLine() is { Length: > 0 } line) handle(line.Split('\t'));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that hung up mid-line: wait for the next one.
            }
        }
    })
    { IsBackground = true, Name = "meter-commands" }.Start();

    /// <summary>Sends a command to the meter. False when no meter is listening.</summary>
    public static bool Send(params string[] parts)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name("commands"), PipeDirection.Out);
            pipe.Connect(500);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            writer.WriteLine(string.Join('\t', parts.Select(p => p.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '))));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsRunning => Read() is not null;

    /// <summary>A setting changed in settings.ini by the dashboard: written, then the meter told.</summary>
    public static void ChangeSettings(Action<MeterSettings> change)
    {
        var settings = MeterSettings.Load();
        change(settings);
        settings.Save();
        Send("reload");
    }
}
