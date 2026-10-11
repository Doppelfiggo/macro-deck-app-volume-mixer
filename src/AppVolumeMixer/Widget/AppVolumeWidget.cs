using System.Text.Json;
using AppVolumeMixer.Audio;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;

namespace AppVolumeMixer.Widget;

/// <summary>The stored configuration of one App Volume widget.</summary>
internal sealed record AppVolumeWidgetData(string App, bool Vertical, bool Relative, bool ShowName, bool ShowValue)
{
	public const string AppKey = "app";
	public const string OrientationKey = "orientation";
	public const string InteractionKey = "interaction";
	public const string ShowNameKey = "showName";
	public const string ShowValueKey = "showValue";

	public const string DefaultJson =
		"""{"app":"","orientation":"vertical","interaction":"absolute","showName":true,"showValue":true}""";

	// Every key the config tree or the host's appearance actions may write. Nullable types, because a
	// cleared field may be written as null before it is removed.
	public const string Schema = """
		{
		  "type": "object",
		  "properties": {
		    "app": { "type": ["string", "null"] },
		    "orientation": { "type": ["string", "null"] },
		    "interaction": { "type": ["string", "null"] },
		    "showName": { "type": ["boolean", "null"] },
		    "showValue": { "type": ["boolean", "null"] },
		    "label": { "type": ["string", "null"] },
		    "backgroundColor": { "type": ["string", "null"] },
		    "labelColor": { "type": ["string", "null"] },
		    "accentColor": { "type": ["string", "null"] },
		    "fontFaceId": { "type": ["string", "null"] },
		    "fontSize": { "type": ["number", "null"] },
		    "textAlign": { "type": ["string", "null"] },
		    "labelPosition": { "type": ["string", "null"] },
		    "border": { "type": ["object", "null"] }
		  }
		}
		""";

	public static AppVolumeWidgetData Read(JsonElement data) => new(
		AudioMixer.NormalizeKey(ReadString(data, AppKey)),
		!string.Equals(ReadString(data, OrientationKey), "horizontal", StringComparison.OrdinalIgnoreCase),
		string.Equals(ReadString(data, InteractionKey), "relative", StringComparison.OrdinalIgnoreCase),
		ReadBool(data, ShowNameKey) ?? true,
		ReadBool(data, ShowValueKey) ?? true);

	public static string? ReadString(JsonElement data, string key)
		=> data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) &&
			value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

	public static bool? ReadBool(JsonElement data, string key)
		=> data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) &&
			value.ValueKind is JsonValueKind.True or JsonValueKind.False
				? value.GetBoolean()
				: null;
}

internal static class AppVolumeWidget
{
	private const string DefaultAccent = "#2196f3";
	private const string InactiveColor = "#6b7280";

	/// <summary>The live tile. <paramref name="interactive" /> is false for the editor preview, which
	/// follows the real volume but never changes it.</summary>
	public static UiElement View(AudioMixer mixer, JsonElement data, bool interactive)
	{
		var config = AppVolumeWidgetData.Read(data);
		var appearance = UiWidgetAppearance.Read(data);

		if (config.App.Length == 0)
		{
			return Placeholder(config, appearance);
		}

		var entry = mixer.Get(config.App);

		var name = new UiTextRun
		{
			Key = "name",
			Text = appearance.Label is { Length: > 0 } label
				? UiText.Of(label)
				: config.App == AudioMixer.SystemSoundsKey
					? UiText.Of(Strings.Widgets.AppVolume.SystemSounds())
					: UiText.From(() => entry.DisplayName.Value),
			Size = 0.11,
			MinSize = 0.075,
			MaxLines = 1,
			Weight = UiComponentTextWeights.Medium,
			Role = UiComponentTextRoles.Secondary,
			Color = Color(appearance.LabelColor),
			Align = config.Vertical ? UiComponentAlignments.Center : UiComponentAlignments.Start,
		};

		var value = new UiTextRun
		{
			Key = "value",
			Text = UiText.Optional(() =>
				!entry.Running.Value ? UiText.Of(Strings.Widgets.AppVolume.NotRunning())
				: entry.Muted.Value ? UiText.Of(Strings.Widgets.AppVolume.Muted())
				: UiText.Of($"{Math.Round(entry.Level.Value * 100):0}%")),
			Size = 0.12,
			MinSize = 0.08,
			MaxLines = 1,
			Weight = UiComponentTextWeights.SemiBold,
			Role = UiComponentTextRoles.Primary,
			Color = Color(appearance.LabelColor),
			Align = config.Vertical ? UiComponentAlignments.Center : UiComponentAlignments.End,
		};

		IReadOnlyList<UiEventHandler> events = interactive
			?
			[
				UiEventHandler.On(UiComponentEvents.Adjust, (UiEventData d) => Apply(mixer, entry, d)),
				UiEventHandler.On(UiComponentEvents.Change, (UiEventData d) => Apply(mixer, entry, d)),
				UiEventHandler.On(UiComponentEvents.DoublePress, (UiEventData _) => ToggleMute(mixer, entry)),
			]
			: [];

		var accent = appearance.AccentColor is { Length: > 0 } a ? a : null;
		var track = Track(config, () => entry.Level.Value,
			() => !entry.Running.Value || entry.Muted.Value ? InactiveColor : accent, events);

		var children = new List<UiElement>();

		if (config.Vertical)
		{
			if (config.ShowName)
			{
				children.Add(name);
			}

			children.Add(track);

			if (config.ShowValue)
			{
				children.Add(value);
			}
		}
		else
		{
			var header = new List<UiElement>();

			if (config.ShowName)
			{
				header.Add(name);
			}

			if (config.ShowValue)
			{
				header.Add(value);
			}

			if (header.Count > 0)
			{
				children.Add(new UiStack
				{
					Key = "header",
					Direction = UiComponentDirections.Horizontal,
					Align = UiComponentAlignments.Center,
					Justify = header.Count == 2 ? UiComponentJustify.SpaceBetween
						: config.ShowValue ? UiComponentJustify.End
						: UiComponentJustify.Start,
					Children = header,
				});
			}

			children.Add(track);
		}

		return Root(appearance, children);
	}

	/// <summary>The card in the widget picker: a static sample, nothing live.</summary>
	public static UiElement Sample()
		=> Root(new UiWidgetAppearanceValues(),
		[
			new UiTextRun
			{
				Key = "name",
				Text = Strings.Widgets.AppVolume.SampleLabel(),
				Size = 0.11,
				MinSize = 0.075,
				Weight = UiComponentTextWeights.Medium,
				Role = UiComponentTextRoles.Secondary,
				Align = UiComponentAlignments.Center,
			},
			Track(new AppVolumeWidgetData("", true, false, true, true), () => 0.65, () => null, []),
			new UiTextRun
			{
				Key = "value",
				Text = "65%",
				Size = 0.12,
				MinSize = 0.08,
				Weight = UiComponentTextWeights.SemiBold,
				Role = UiComponentTextRoles.Primary,
				Align = UiComponentAlignments.Center,
			},
		]);

	/// <summary>The <c>widget-config</c> editor: which app, the layout, and the standard appearance fields.</summary>
	public static UiElement Config(AudioMixer mixer, JsonElement data)
	{
		var current = AppVolumeWidgetData.Read(data);

		var app = new UiState<string>(current.App);
		var orientation = new UiState<string>(current.Vertical ? "vertical" : "horizontal");
		var interaction = new UiState<string>(current.Relative ? "relative" : "absolute");
		var showName = new UiState<bool>(current.ShowName);
		var showValue = new UiState<bool>(current.ShowValue);

		var options = new UiOptionsState(UiOptionSource.From((_, _) =>
			Task.FromResult(new UiOptionResult
			{
				Options = AppOptions(mixer, app.Peek()),
				AllowsCustomValue = true,
			})));
		options.Reload();

		return new UiWidgetConfiguration
		{
			Key = "root",
			Properties = new UiWidgetProperties
			{
				Key = "properties",
				Children =
				[
					new UiHeading { Key = "app-heading", Text = Strings.Widgets.AppVolume.Config.AppHeading() },
					new UiDynamicChoiceInput
					{
						Key = AppVolumeWidgetData.AppKey,
						Label = Strings.Widgets.AppVolume.Config.AppLabel(),
						Description = Strings.Widgets.AppVolume.Config.AppDescription(),
						Placeholder = Strings.Widgets.AppVolume.Config.AppPlaceholder(),
						Binding = Bind.Custom(() => app.Value, v => app.Value = AudioMixer.NormalizeKey(v)),
						OptionsState = options,
						Events =
						[
							UiEventHandler.On(UiConfigEvents.Open, () => options.Reload()),
							UiEventHandler.On(UiConfigEvents.Reload, () => options.Reload()),
						],
					},
					new UiHeading { Key = "layout-heading", Text = Strings.Widgets.AppVolume.Config.LayoutHeading() },
					new UiChoiceInput
					{
						Key = AppVolumeWidgetData.OrientationKey,
						Label = Strings.Widgets.AppVolume.Config.Orientation(),
						Binding = Bind.To(orientation),
						Segmented = true,
						Options = UiValue.Of<IReadOnlyList<UiOption>>(
						[
							UiOption.Of("vertical", Strings.Widgets.AppVolume.Config.Vertical()),
							UiOption.Of("horizontal", Strings.Widgets.AppVolume.Config.Horizontal()),
						]),
					},
					new UiChoiceInput
					{
						Key = AppVolumeWidgetData.InteractionKey,
						Label = Strings.Widgets.AppVolume.Config.Interaction(),
						Description = Strings.Widgets.AppVolume.Config.InteractionDescription(),
						Binding = Bind.To(interaction),
						Options = UiValue.Of<IReadOnlyList<UiOption>>(
						[
							UiOption.Of("absolute", Strings.Widgets.AppVolume.Config.Absolute()),
							UiOption.Of("relative", Strings.Widgets.AppVolume.Config.Relative()),
						]),
					},
					new UiBooleanInput
					{
						Key = AppVolumeWidgetData.ShowNameKey,
						Label = Strings.Widgets.AppVolume.Config.ShowName(),
						Binding = Bind.To(showName),
					},
					new UiBooleanInput
					{
						Key = AppVolumeWidgetData.ShowValueKey,
						Label = Strings.Widgets.AppVolume.Config.ShowValue(),
						Binding = Bind.To(showValue),
					},
					new UiProse { Key = "mute-hint", Text = Strings.Widgets.AppVolume.Config.MuteHint() },
					UiWidgetAppearance.Section(data,
						UiWidgetAppearanceFields.Label | UiWidgetAppearanceFields.LabelColor |
						UiWidgetAppearanceFields.AccentColor | UiWidgetAppearanceFields.BackgroundColor |
						UiWidgetAppearanceFields.Border),
				],
			},
		};
	}

	private static IReadOnlyList<UiOption> AppOptions(AudioMixer mixer, string selected)
	{
		var options = new List<UiOption>();

		foreach (var app in mixer.Apps)
		{
			var label = app.Key == AudioMixer.SystemSoundsKey
				? Strings.Widgets.AppVolume.SystemSounds()
				: (MacroDeck.Localization.LocalizedText)app.DisplayName;

			options.Add(UiOption.Of(app.Key, label) with
			{
				Description = app.Key == AudioMixer.SystemSoundsKey ? default : $"{app.Key}.exe",
			});
		}

		if (selected.Length > 0 && options.All(o => o.Value != selected))
		{
			options.Insert(0, UiOption.Of(selected, AudioMixer.PrettyName(selected)) with
			{
				Badge = Strings.Widgets.AppVolume.NotRunning(),
				Description = $"{selected}.exe",
			});
		}

		return options;
	}

	private static UiElement Placeholder(AppVolumeWidgetData config, UiWidgetAppearanceValues appearance)
		=> Root(appearance,
		[
			new UiTextRun
			{
				Key = "hint",
				Text = Strings.Widgets.AppVolume.PickApp(),
				Size = 0.11,
				MinSize = 0.07,
				Wrap = true,
				Role = UiComponentTextRoles.Secondary,
				Color = Color(appearance.LabelColor),
				Align = UiComponentAlignments.Center,
			},
			Track(config, () => 0, () => InactiveColor, []),
		]);

	private static UiStack Root(UiWidgetAppearanceValues appearance, IReadOnlyList<UiElement> children)
		=> new()
		{
			Key = "app-volume",
			Direction = UiComponentDirections.Vertical,
			Padding = 0.08,
			Gap = 0.04,
			Background = Color(appearance.BackgroundColor),
			Children = children,
		};

	private static UiSlider Track(AppVolumeWidgetData config, Func<double> level, Func<string?> color,
		IReadOnlyList<UiEventHandler> events)
	{
		// Thin enough that the thumb reads as a knob on a track, matching Macro Deck's own Slider widget.
		var thickness = config.Vertical ? UiSize.FromBasis(0.11, 0.35) : UiSize.FromBasis(0.09, 0.4);
		var fallbackColor = UiValue.From(() => color() ?? DefaultAccent);

		return new UiSlider
		{
			Key = "track",
			Fill = true,
			Direction = config.Vertical ? UiValue.Of(UiComponentDirections.Vertical) : UiValue.None<string>(),
			Interaction = config.Relative
				? UiValue.Of(UiComponentSliderInteractions.Relative)
				: UiValue.None<string>(),
			Level = UiValue.From(level),
			Step = 0.01,
			LevelColor = UiValue.Optional(() => color() is { } c ? UiValue.Of(c) : UiValue.None<string>()),
			Thickness = thickness,
			Events = events,
			Fallback = new UiRangeBar
			{
				Key = "track-fallback",
				Thickness = thickness,
				Start = 0,
				End = UiValue.From(level),
				StartColor = fallbackColor,
				EndColor = fallbackColor,
			},
		};
	}

	private static UiEventOutcome Apply(AudioMixer mixer, AppVolumeEntry entry, UiEventData data)
	{
		if (!data.TryGetDouble(out var level))
		{
			return UiEventOutcome.Rejected("The event payload is not a number.");
		}

		if (!entry.Running.Peek())
		{
			return UiEventOutcome.Rejected("The application is not running.");
		}

		mixer.SetVolume(entry.Key, level);
		return UiEventOutcome.Accepted;
	}

	private static UiEventOutcome ToggleMute(AudioMixer mixer, AppVolumeEntry entry)
	{
		if (!entry.Running.Peek())
		{
			return UiEventOutcome.Rejected("The application is not running.");
		}

		mixer.ToggleMute(entry.Key);
		return UiEventOutcome.Accepted;
	}

	private static UiValue<string> Color(string? color)
		=> color is { Length: > 0 } value ? UiValue.Of(value) : UiValue.None<string>();
}
