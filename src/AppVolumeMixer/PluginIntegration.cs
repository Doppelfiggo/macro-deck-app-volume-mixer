using System.Text.Json;
using AppVolumeMixer.Audio;
using AppVolumeMixer.Widget;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using Serilog;

namespace AppVolumeMixer;

/// <summary>
/// Registers the "App Volume" widget type: a slider tile bound to one application in the Windows Volume
/// Mixer. Dragging it sets that app's volume; changes made in Windows show up on the tile.
/// </summary>
public sealed class PluginIntegration : IPluginIntegration, IWidgetTypeProvider, IUiProvider
{
	// Persisted in every placed widget as "<plugin id>::app-volume" - never rename.
	private const string WidgetLocalId = "app-volume";
	private const string QualifiedWidgetTypeId = "com.yanick.app-volume-mixer::" + WidgetLocalId;

	private readonly ILogger _logger;
	private readonly AudioMixer _mixer;
	private string? _widgetTypeId;

	public PluginIntegration(ILogger logger)
	{
		_logger = logger.ForContext<PluginIntegration>();
		_mixer = new AudioMixer(logger);
	}

	public IReadOnlyList<IActionDefinition> Actions { get; } = [];

	public string ProviderName => string.Empty;

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
	[
		new UiSurfaceDeclaration { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
		new UiSurfaceDeclaration { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
		new UiSurfaceDeclaration { Kind = UiSurfaceKinds.Config, SessionMode = UiSessionModes.Exclusive },
	];

	// Runs on every (re)connect, so it must be idempotent: Start() is a no-op while already running.
	public Task InitializeAsync(IIntegrationContext context)
	{
		_mixer.Start();
		_logger.Information("App Volume Mixer initialized");
		return Task.CompletedTask;
	}

	public Task ShutdownAsync()
	{
		_mixer.Stop();
		return Task.CompletedTask;
	}

	public async Task InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken = default)
	{
		var registration = await context.RegisterWidgetTypeAsync(Descriptor(), cancellationToken).ConfigureAwait(false);
		_widgetTypeId = registration.WidgetTypeId;
		_logger.Information("Registered widget type {WidgetType}", _widgetTypeId);
	}

	public IReadOnlyList<WidgetTypeDescriptor> GetWidgetTypes() => [Descriptor()];

	private static WidgetTypeDescriptor Descriptor()
		=> new(
			WidgetLocalId,
			Strings.Widgets.AppVolume.Name(),
			Strings.Widgets.AppVolume.Description(),
			DefaultData: AppVolumeWidgetData.DefaultJson,
			DataSchema: AppVolumeWidgetData.Schema,
			HasConfiguration: true)
		{
			AppearanceProperties =
			[
				WidgetAppearanceProperty.BackgroundColor,
				WidgetAppearanceProperty.Label,
				WidgetAppearanceProperty.LabelColor,
				WidgetAppearanceProperty.AccentColor,
			],
		};

	public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
	{
		var surface = request.Surface;
		var attributes = surface.Attributes;
		UiElement? root = null;

		try
		{
			if (surface.Kind is UiSurfaceKinds.Widget or UiSurfaceKinds.Preview &&
				attributes.TryGetValue(UiWidgetSurfaceAttributes.WidgetType, out var type) && IsOurs(type))
			{
				attributes.TryGetValue(UiWidgetSurfaceAttributes.Data, out var data);
				var sample = attributes.TryGetValue(UiWidgetSurfaceAttributes.Sample, out var flag) &&
					flag.ValueKind == JsonValueKind.True;

				root = sample
					? AppVolumeWidget.Sample()
					: AppVolumeWidget.View(_mixer, data, interactive: surface.Kind == UiSurfaceKinds.Widget);
			}
			else if (surface.Kind == UiSurfaceKinds.Config &&
				attributes.TryGetValue(UiConfigSurfaceAttributes.EntryPoint, out var entryPoint) &&
				entryPoint.ValueKind == JsonValueKind.String &&
				entryPoint.GetString() == UiConfigEntryPoints.WidgetConfig &&
				attributes.TryGetValue(UiConfigSurfaceAttributes.WidgetType, out var configType) && IsOurs(configType))
			{
				attributes.TryGetValue(UiConfigSurfaceAttributes.WidgetData, out var data);
				root = AppVolumeWidget.Config(_mixer, data);
			}
		}
		catch (Exception e)
		{
			_logger.Error(e, "Could not build the {Surface} surface", surface.Kind);
			root = null;
		}

		return Task.FromResult<IUiSession?>(root is null ? null : new ViewSession(new UiView(surface, root)));
	}

	private bool IsOurs(JsonElement type)
	{
		if (type.ValueKind != JsonValueKind.String || type.GetString() is not { } value)
		{
			return false;
		}

		return value == _widgetTypeId || value == QualifiedWidgetTypeId;
	}
}
