using System.Runtime.InteropServices;

namespace AppVolumeMixer.Audio;

// Minimal Windows Core Audio (WASAPI session) COM definitions - just what the Volume Mixer needs:
// enumerate render endpoints, enumerate their audio sessions, read/write each session's volume and mute.
// Inherited interface members are re-declared in vtable order, as [ComImport] requires.

internal enum EDataFlow
{
	Render = 0,
	Capture = 1,
	All = 2,
}

internal enum AudioSessionState
{
	Inactive = 0,
	Active = 1,
	Expired = 2,
}

internal static class CoreAudioConstants
{
	public const uint DeviceStateActive = 0x00000001;
	public const int ClsctxAll = 0x17;

	public static readonly Guid IidAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject;

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
	[PreserveSig]
	int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

	[PreserveSig]
	int GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice endpoint);

	[PreserveSig]
	int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

	[PreserveSig]
	int RegisterEndpointNotificationCallback(IntPtr client);

	[PreserveSig]
	int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
	[PreserveSig]
	int GetCount(out uint count);

	[PreserveSig]
	int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
	[PreserveSig]
	int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
		[MarshalAs(UnmanagedType.IUnknown)] out object instance);

	[PreserveSig]
	int OpenPropertyStore(int access, out IntPtr properties);

	[PreserveSig]
	int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

	[PreserveSig]
	int GetState(out uint state);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
	// IAudioSessionManager
	[PreserveSig]
	int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IntPtr sessionControl);

	[PreserveSig]
	int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out IntPtr audioVolume);

	// IAudioSessionManager2
	[PreserveSig]
	int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

	[PreserveSig]
	int RegisterSessionNotification(IAudioSessionNotification sessionNotification);

	[PreserveSig]
	int UnregisterSessionNotification(IAudioSessionNotification sessionNotification);

	[PreserveSig]
	int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);

	[PreserveSig]
	int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionNotification
{
	[PreserveSig]
	int OnSessionCreated(IntPtr newSession);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
	[PreserveSig]
	int GetCount(out int sessionCount);

	/// <summary>Returns the raw IAudioSessionControl pointer (caller owns one reference).</summary>
	[PreserveSig]
	int GetSession(int sessionIndex, out IntPtr session);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
	// IAudioSessionControl
	[PreserveSig]
	int GetState(out AudioSessionState state);

	[PreserveSig]
	int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

	[PreserveSig]
	int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

	[PreserveSig]
	int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

	[PreserveSig]
	int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

	[PreserveSig]
	int GetGroupingParam(out Guid groupingParam);

	[PreserveSig]
	int SetGroupingParam(ref Guid overrideValue, ref Guid eventContext);

	[PreserveSig]
	int RegisterAudioSessionNotification(IntPtr newNotifications);

	[PreserveSig]
	int UnregisterAudioSessionNotification(IntPtr newNotifications);

	// IAudioSessionControl2
	[PreserveSig]
	int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

	[PreserveSig]
	int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

	[PreserveSig]
	int GetProcessId(out uint retVal);

	/// <summary>S_OK (0) when this is the system sounds session, S_FALSE (1) otherwise.</summary>
	[PreserveSig]
	int IsSystemSoundsSession();

	[PreserveSig]
	int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

/// <summary>
/// A session's ISimpleAudioVolume (the per-app slider in the Volume Mixer), called straight through its
/// vtable so every call returns its HRESULT and the reference is released deterministically.
/// </summary>
internal sealed unsafe class RawSimpleAudioVolume : IDisposable
{
	private static readonly Guid Iid = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

	private IntPtr _pointer;

	private RawSimpleAudioVolume(IntPtr pointer) => _pointer = pointer;

	/// <summary>Queries <paramref name="sessionControl" /> for ISimpleAudioVolume.</summary>
	public static RawSimpleAudioVolume? From(IntPtr sessionControl, out int hr)
	{
		var iid = Iid;
		hr = Marshal.QueryInterface(sessionControl, in iid, out var pointer);

		return hr == 0 && pointer != IntPtr.Zero ? new RawSimpleAudioVolume(pointer) : null;
	}

	private void** VTable => *(void***)_pointer;

	public int SetMasterVolume(float level, ref Guid eventContext)
	{
		if (_pointer == IntPtr.Zero) return unchecked((int)0x80004003);

		fixed (Guid* context = &eventContext)
		{
			return ((delegate* unmanaged[Stdcall]<IntPtr, float, Guid*, int>)VTable[3])(_pointer, level, context);
		}
	}

	public int GetMasterVolume(out float level)
	{
		float value = 0;
		var hr = _pointer == IntPtr.Zero
			? unchecked((int)0x80004003)
			: ((delegate* unmanaged[Stdcall]<IntPtr, float*, int>)VTable[4])(_pointer, &value);
		level = value;
		return hr;
	}

	public int SetMute(bool mute, ref Guid eventContext)
	{
		if (_pointer == IntPtr.Zero) return unchecked((int)0x80004003);

		fixed (Guid* context = &eventContext)
		{
			return ((delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, int>)VTable[5])(_pointer, mute ? 1 : 0, context);
		}
	}

	public int GetMute(out bool mute)
	{
		var value = 0;
		var hr = _pointer == IntPtr.Zero
			? unchecked((int)0x80004003)
			: ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)VTable[6])(_pointer, &value);
		mute = value != 0;
		return hr;
	}

	public void Dispose()
	{
		var pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);

		if (pointer != IntPtr.Zero)
		{
			Marshal.Release(pointer);
		}
	}
}

/// <summary>A no-op sink: registering one makes Windows keep the session list fresh, and it wakes the
/// poller so a newly started app shows up right away.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class SessionCreatedSink : IAudioSessionNotification
{
	private readonly Action _onCreated;

	public SessionCreatedSink(Action onCreated) => _onCreated = onCreated;

	public int OnSessionCreated(IntPtr newSession)
	{
		try
		{
			_onCreated();
		}
		catch
		{
			// Never let an exception cross back into the audio service.
		}

		return 0;
	}
}

internal static class NativeProcess
{
	private const uint ProcessQueryLimitedInformation = 0x1000;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName,
		ref uint size);

	/// <summary>The full image path of a process, readable even for most elevated processes.</summary>
	public static string? GetImagePath(uint processId)
	{
		var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);

		if (handle == IntPtr.Zero)
		{
			return null;
		}

		try
		{
			var buffer = new char[1024];
			var size = (uint)buffer.Length;

			return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
		}
		finally
		{
			CloseHandle(handle);
		}
	}
}
