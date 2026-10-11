using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MacroDeck.Ui.Runtime;
using Serilog;

namespace AppVolumeMixer.Audio;

/// <summary>One application as the Volume Mixer sees it.</summary>
/// <param name="Key">Stable id stored in widget data: the lower-case process name without ".exe"
/// (e.g. "discord", "spotify"), or <see cref="AudioMixer.SystemSoundsKey" />.</param>
public sealed record AudioApp(string Key, string DisplayName);

/// <summary>Live, UI-bindable state of one application's volume. Shared by every widget bound to the same
/// app, so all of them move together.</summary>
public sealed class AppVolumeEntry
{
	internal AppVolumeEntry(string key, string displayName)
	{
		Key = key;
		DisplayName = new UiState<string>(displayName);
	}

	public string Key { get; }

	/// <summary>Volume as a 0..1 fraction (what the Volume Mixer shows as 0..100).</summary>
	public UiState<double> Level { get; } = new(0);

	public UiState<bool> Muted { get; } = new(false);

	/// <summary>Whether the app currently has an audio session (i.e. appears in the Volume Mixer).</summary>
	public UiState<bool> Running { get; } = new(false);

	public UiState<string> DisplayName { get; }

	// Environment.TickCount64 until which polled readings must not override what the user just set.
	internal long HoldUntil;
}

/// <summary>
/// Talks to the Windows Core Audio session API. All COM work happens on one dedicated MTA thread, which
/// applies queued volume changes immediately and polls every audio session on every active playback device
/// twice a second, so changes made in Windows (or by the app itself) show up on the deck too.
/// </summary>
public sealed class AudioMixer : IDisposable
{
	public const string SystemSoundsKey = "@system";

	private const int PollIntervalMs = 400;
	private const int DeviceScanIntervalMs = 3000;
	private const int HoldMs = 1200;

	private static Guid _eventContext = new("6B1F0C35-7D8E-4C3B-9E0A-5A2D4C7E9F11");

	private readonly ILogger _logger;
	private readonly ConcurrentDictionary<string, AppVolumeEntry> _entries = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, double> _pendingVolume = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, bool> _pendingMute = new(StringComparer.Ordinal);
	private readonly AutoResetEvent _wake = new(false);
	private readonly Lock _lifecycle = new();

	private volatile IReadOnlyList<AudioApp> _apps = [];
	private volatile IReadOnlyDictionary<string, Aggregate> _lastAggregate = new Dictionary<string, Aggregate>();
	private volatile bool _forcePoll;
	private volatile bool _stopping;
	private Thread? _thread;

	public AudioMixer(ILogger logger) => _logger = logger.ForContext<AudioMixer>();

	/// <summary>Apps that currently have an audio session, sorted by name.</summary>
	public IReadOnlyList<AudioApp> Apps => _apps;

	public static string NormalizeKey(string? raw)
	{
		var key = (raw ?? string.Empty).Trim();

		if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			key = key[..^4];
		}

		return key.ToLowerInvariant();
	}

	public static string PrettyName(string key)
		=> key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..];

	public void Start()
	{
		lock (_lifecycle)
		{
			if (_thread is { IsAlive: true })
			{
				return;
			}

			_stopping = false;
			_thread = new Thread(Run) { IsBackground = true, Name = "AppVolumeMixer.CoreAudio" };
			_thread.SetApartmentState(ApartmentState.MTA);
			_thread.Start();
		}
	}

	public void Stop()
	{
		Thread? thread;

		lock (_lifecycle)
		{
			thread = _thread;
			_thread = null;
			_stopping = true;
		}

		_wake.Set();
		thread?.Join(TimeSpan.FromSeconds(3));
	}

	public void Dispose()
	{
		Stop();
		_wake.Dispose();
	}

	/// <summary>The shared live state for one app; created on first use.</summary>
	public AppVolumeEntry Get(string key)
	{
		return _entries.GetOrAdd(key, static (k, self) =>
		{
			var entry = new AppVolumeEntry(k, k == SystemSoundsKey ? "System sounds" : PrettyName(k));

			if (self._lastAggregate.TryGetValue(k, out var current))
			{
				entry.DisplayName.Value = current.DisplayName;
				entry.Level.Value = current.Volume;
				entry.Muted.Value = current.Muted;
				entry.Running.Value = true;
			}

			return entry;
		}, this);
	}

	/// <summary>Sets an app's volume (0..1). The UI state moves immediately; the change reaches Windows a
	/// moment later on the audio thread.</summary>
	public void SetVolume(string key, double level)
	{
		level = Math.Round(Math.Clamp(level, 0, 1), 3);
		var entry = Get(key);

		entry.HoldUntil = Environment.TickCount64 + HoldMs;
		entry.Level.Value = level;
		_pendingVolume[key] = level;

		// Moving a muted slider un-mutes it, like the Windows 11 mixer does.
		if (entry.Muted.Peek() && level > 0)
		{
			entry.Muted.Value = false;
			_pendingMute[key] = false;
		}

		_wake.Set();
	}

	public void ToggleMute(string key)
	{
		var entry = Get(key);
		var muted = !entry.Muted.Peek();

		entry.HoldUntil = Environment.TickCount64 + HoldMs;
		entry.Muted.Value = muted;
		_pendingMute[key] = muted;
		_wake.Set();
	}

	// ---------------------------------------------------------------- audio thread

	private sealed record Aggregate(string DisplayName, double Volume, bool Muted);

	private sealed class SessionHandle
	{
		public required string Key { get; init; }
		public required string DisplayName { get; init; }
		public required object Com { get; init; }
		public required RawSimpleAudioVolume Volume { get; init; }
		public required bool Active { get; init; }
	}

	private sealed class DeviceHandle
	{
		public required IAudioSessionManager2 Manager { get; init; }
		public SessionCreatedSink? Sink { get; set; }
		public bool Seen { get; set; }
	}

	private readonly Dictionary<string, DeviceHandle> _devices = new(StringComparer.Ordinal);
	private readonly Dictionary<uint, (string Key, string DisplayName)> _processCache = [];
	private List<SessionHandle> _sessions = [];
	private IMMDeviceEnumerator? _enumerator;
	private long _nextDeviceScan;
	private long _lastErrorLog;
	private string? _lastSummary;

	private void Run()
	{
		try
		{
			_enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
		}
		catch (Exception e)
		{
			_logger.Error(e, "Could not create the Windows audio device enumerator");
			return;
		}

		_logger.Information("Audio thread started");
		long nextPoll = 0;

		while (!_stopping)
		{
			try
			{
				ApplyPendingWrites();

				var now = Environment.TickCount64;

				if (_forcePoll || now >= nextPoll)
				{
					_forcePoll = false;
					Poll();
					nextPoll = Environment.TickCount64 + PollIntervalMs;
				}
			}
			catch (Exception e)
			{
				LogThrottled(e, "Audio poll failed");
				nextPoll = Environment.TickCount64 + PollIntervalMs;
			}

			var wait = Math.Clamp(nextPoll - Environment.TickCount64, 0, PollIntervalMs);
			_wake.WaitOne((int)wait);
		}

		Cleanup();
		_logger.Information("Audio thread stopped");
	}

	private void ApplyPendingWrites()
	{
		foreach (var key in _pendingVolume.Keys)
		{
			if (!_pendingVolume.TryRemove(key, out var level))
			{
				continue;
			}

			foreach (var session in _sessions)
			{
				if (session.Key == key)
				{
					session.Volume.SetMasterVolume((float)level, ref _eventContext);
				}
			}
		}

		foreach (var key in _pendingMute.Keys)
		{
			if (!_pendingMute.TryRemove(key, out var muted))
			{
				continue;
			}

			foreach (var session in _sessions)
			{
				if (session.Key == key)
				{
					session.Volume.SetMute(muted, ref _eventContext);
				}
			}
		}
	}

	private void Poll()
	{
		if (Environment.TickCount64 >= _nextDeviceScan)
		{
			ScanDevices();
			_nextDeviceScan = Environment.TickCount64 + DeviceScanIntervalMs;
		}

		var fresh = new List<SessionHandle>();
		var seenPids = new HashSet<uint>();
		var rawSessions = 0;
		var rejected = 0;

		foreach (var device in _devices.Values)
		{
			var enumHr = device.Manager.GetSessionEnumerator(out var sessionEnumerator);

			if (enumHr != 0 || sessionEnumerator is null)
			{
				LogThrottled(null, $"GetSessionEnumerator failed: 0x{enumHr:X8}");
				continue;
			}

			try
			{
				sessionEnumerator.GetCount(out var count);
				rawSessions += count;

				for (var i = 0; i < count; i++)
				{
					if (sessionEnumerator.GetSession(i, out var raw) != 0 || raw == IntPtr.Zero)
					{
						continue;
					}

					var handle = TryCreateHandle(raw, seenPids);

					if (handle is null)
					{
						rejected++;
					}
					else
					{
						fresh.Add(handle);
					}
				}
			}
			finally
			{
				Release(sessionEnumerator);
			}
		}

		var old = _sessions;
		_sessions = fresh;

		foreach (var session in old)
		{
			session.Volume.Dispose();
			Release(session.Com);
		}

		foreach (var pid in _processCache.Keys.Where(pid => !seenPids.Contains(pid)).ToList())
		{
			_processCache.Remove(pid);
		}

		Publish(fresh);

		var summary = $"{_devices.Count} device(s), {rawSessions} session(s), {rejected} skipped" +
			(_rejectReasons.Count > 0 ? " [" + string.Join("; ", _rejectReasons.Select(r => $"{r.Key} x{r.Value}")) + "]" : "") +
			": " + string.Join(", ", _apps.Select(a => a.Key));
		_rejectReasons.Clear();

		if (summary != _lastSummary)
		{
			_lastSummary = summary;
			_logger.Information("Audio sessions: {Summary}", summary);
		}
	}

	private readonly Dictionary<string, int> _rejectReasons = [];

	private void Reject(string reason) => _rejectReasons[reason] = _rejectReasons.GetValueOrDefault(reason) + 1;

	/// <summary>Wraps one raw IAudioSessionControl pointer; takes ownership of its reference either way.</summary>
	private SessionHandle? TryCreateHandle(IntPtr raw, HashSet<uint> seenPids)
	{
		object com;
		RawSimpleAudioVolume? volume;
		int qiHr;

		try
		{
			com = Marshal.GetObjectForIUnknown(raw);
			volume = RawSimpleAudioVolume.From(raw, out qiHr);
		}
		finally
		{
			Marshal.Release(raw);
		}

		if (com is not IAudioSessionControl2 control)
		{
			Reject("no IAudioSessionControl2");
			volume?.Dispose();
			Release(com);
			return null;
		}

		if (volume is null)
		{
			Reject($"no ISimpleAudioVolume hr=0x{qiHr:X8}");
			Release(com);
			return null;
		}

		var handle = Describe(com, control, volume, seenPids);

		if (handle is null)
		{
			volume.Dispose();
			Release(com);
		}

		return handle;
	}

	private SessionHandle? Describe(object com, IAudioSessionControl2 control, RawSimpleAudioVolume volume,
		HashSet<uint> seenPids)
	{
		var stateHr = control.GetState(out var state);

		if (stateHr != 0 || state == AudioSessionState.Expired)
		{
			Reject($"state hr=0x{stateHr:X8} state={state}");
			return null;
		}

		string key;
		string displayName;

		if (control.IsSystemSoundsSession() == 0)
		{
			key = SystemSoundsKey;
			displayName = "System sounds";
		}
		else
		{
			// AUDCLNT_S_NO_SINGLE_PROCESS (0x0889000D) is a success code: the session spans several
			// processes but still reports the owning one.
			var pidHr = control.GetProcessId(out var pid);

			if (pidHr < 0 || pid == 0)
			{
				Reject($"pid hr=0x{pidHr:X8} pid={pid}");
				return null;
			}

			seenPids.Add(pid);

			if (!_processCache.TryGetValue(pid, out var info))
			{
				info = DescribeProcess(pid, control);

				if (info.Key.Length == 0)
				{
					Reject($"no process name for pid {pid}");
					return null;
				}

				_processCache[pid] = info;
			}

			(key, displayName) = info;
		}

		return new SessionHandle
		{
			Key = key,
			DisplayName = displayName,
			Com = com,
			Volume = volume,
			Active = state == AudioSessionState.Active,
		};
	}

	private static (string Key, string DisplayName) DescribeProcess(uint pid, IAudioSessionControl2 control)
	{
		string? path = null;
		string? name = null;

		try
		{
			path = NativeProcess.GetImagePath(pid);
		}
		catch
		{
			// Fall back to the process name below.
		}

		if (!string.IsNullOrEmpty(path))
		{
			name = Path.GetFileNameWithoutExtension(path);
		}
		else
		{
			try
			{
				using var process = Process.GetProcessById((int)pid);
				name = process.ProcessName;
			}
			catch
			{
				return (string.Empty, string.Empty);
			}
		}

		var key = NormalizeKey(name);
		string? display = null;

		if (!string.IsNullOrEmpty(path))
		{
			try
			{
				var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();

				if (!string.IsNullOrEmpty(description))
				{
					display = description;
				}
			}
			catch
			{
				// No version resource: keep the fallback.
			}
		}

		if (display is null && control.GetDisplayName(out var sessionName) == 0 &&
			!string.IsNullOrWhiteSpace(sessionName) && !sessionName.StartsWith('@'))
		{
			display = sessionName.Trim();
		}

		return (key, display ?? PrettyName(name ?? key));
	}

	private void Publish(List<SessionHandle> sessions)
	{
		var aggregate = new Dictionary<string, Aggregate>(StringComparer.Ordinal);

		foreach (var group in sessions.GroupBy(s => s.Key))
		{
			// An app can own several sessions (several processes or devices). Report the one actually
			// playing, and call it muted only when every session is muted.
			var primary = group.FirstOrDefault(s => s.Active) ?? group.First();
			primary.Volume.GetMasterVolume(out var level);

			var allMuted = true;

			foreach (var session in group)
			{
				if (session.Volume.GetMute(out var muted) != 0 || !muted)
				{
					allMuted = false;
				}
			}

			aggregate[group.Key] = new Aggregate(primary.DisplayName, Math.Round(level, 3), allMuted);
		}

		_lastAggregate = aggregate;
		_apps =
		[
			.. aggregate
				.Select(pair => new AudioApp(pair.Key, pair.Value.DisplayName))
				.OrderBy(app => app.Key == SystemSoundsKey ? 1 : 0)
				.ThenBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase),
		];

		var now = Environment.TickCount64;

		foreach (var entry in _entries.Values)
		{
			if (aggregate.TryGetValue(entry.Key, out var current))
			{
				entry.Running.Value = true;
				entry.DisplayName.Value = current.DisplayName;

				if (now >= entry.HoldUntil)
				{
					entry.Level.Value = current.Volume;
					entry.Muted.Value = current.Muted;
				}
			}
			else
			{
				entry.Running.Value = false;
			}
		}
	}

	private void ScanDevices()
	{
		foreach (var device in _devices.Values)
		{
			device.Seen = false;
		}

		var endpointsHr = _enumerator!.EnumAudioEndpoints(EDataFlow.Render, CoreAudioConstants.DeviceStateActive,
			out var collection);

		if (endpointsHr != 0)
		{
			LogThrottled(null, $"EnumAudioEndpoints failed: 0x{endpointsHr:X8}");
		}

		if (endpointsHr == 0 && collection is not null)
		{
			try
			{
				collection.GetCount(out var count);

				for (uint i = 0; i < count; i++)
				{
					if (collection.Item(i, out var device) != 0 || device is null)
					{
						continue;
					}

					try
					{
						if (device.GetId(out var id) != 0 || id is null)
						{
							continue;
						}

						if (_devices.TryGetValue(id, out var known))
						{
							known.Seen = true;
							continue;
						}

						var iid = CoreAudioConstants.IidAudioSessionManager2;

						var activateHr = device.Activate(ref iid, CoreAudioConstants.ClsctxAll, IntPtr.Zero, out var instance);

						if (activateHr != 0 || instance is not IAudioSessionManager2 manager)
						{
							_logger.Warning("Could not open audio sessions of device {DeviceId}: 0x{Hr:X8}", id, activateHr);
							continue;
						}

						var handle = new DeviceHandle { Manager = manager, Seen = true };

						try
						{
							var sink = new SessionCreatedSink(() =>
							{
								_forcePoll = true;
								_wake.Set();
							});

							if (manager.RegisterSessionNotification(sink) == 0)
							{
								handle.Sink = sink;
							}
						}
						catch (Exception e)
						{
							LogThrottled(e, "Could not register for new audio sessions; falling back to polling");
						}

						_devices[id] = handle;
						_logger.Debug("Watching playback device {DeviceId}", id);
					}
					finally
					{
						Release(device);
					}
				}
			}
			finally
			{
				Release(collection);
			}
		}

		foreach (var (id, device) in _devices.Where(pair => !pair.Value.Seen).ToList())
		{
			ReleaseDevice(device);
			_devices.Remove(id);
			_logger.Debug("Playback device {DeviceId} went away", id);
		}
	}

	private void ReleaseDevice(DeviceHandle device)
	{
		try
		{
			if (device.Sink is not null)
			{
				device.Manager.UnregisterSessionNotification(device.Sink);
			}
		}
		catch
		{
			// The device may already be gone.
		}

		Release(device.Manager);
	}

	private void Cleanup()
	{
		foreach (var session in _sessions)
		{
			session.Volume.Dispose();
			Release(session.Com);
		}

		_sessions = [];

		foreach (var device in _devices.Values)
		{
			ReleaseDevice(device);
		}

		_devices.Clear();
		_processCache.Clear();

		if (_enumerator is not null)
		{
			Release(_enumerator);
			_enumerator = null;
		}
	}

	private static void Release(object? com)
	{
		if (com is not null && Marshal.IsComObject(com))
		{
			try
			{
				Marshal.ReleaseComObject(com);
			}
			catch
			{
				// Already released.
			}
		}
	}

	private void LogThrottled(Exception? e, string message)
	{
		var now = Environment.TickCount64;

		if (now - _lastErrorLog > 30_000)
		{
			_lastErrorLog = now;
			_logger.Warning(e, message);
		}
	}
}
