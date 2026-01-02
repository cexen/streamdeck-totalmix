using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace streamdeck_totalmix
{
    [PluginActionId("de.shells.totalmix.oscdial.action")]
    public class OscDial : EncoderBase
    {
        private const string LayoutPath = "layouts/osc-dial.json";
        private const float DefaultStep = 0.02f;
        // Slightly above 0 dB on TotalMix's 0..1 scale to avoid "-0.0 dB" display
        private const float UnityFaderValue = 836.5f / 1023f;
        private const int LocalValueMaxMs = 1000;
        private const float LocalValueEpsilon = 0.002f;

        private class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings()
            {
                PluginSettings instance = new PluginSettings
                {
                    Name = "/1/volume1",
                    SelectedAction = "1",
                    Bus = "Input",
                    DisplayChannelName = true,
                    ChannelCount = Globals.channelCount,
                    DialStep = DefaultStep
                };
                return instance;
            }

            [FilenameProperty]
            [JsonProperty(PropertyName = "Name")]
            public string Name { get; set; }

            [JsonProperty(PropertyName = "SelectedAction")]
            public string SelectedAction { get; set; }

            [JsonProperty(PropertyName = "Bus")]
            public string Bus { get; set; }

            [JsonProperty(PropertyName = "DisplayChannelName")]
            public bool DisplayChannelName { get; set; }

            [JsonProperty(PropertyName = "ChannelCount")]
            public Int32 ChannelCount { get; set; }

            [JsonProperty(PropertyName = "DialStep")]
            public float DialStep { get; set; }

        }

        private PluginSettings settings;
        private string lastFeedbackChannel = String.Empty;
        private string lastFeedbackMute = String.Empty;
        private string lastFeedbackValue = String.Empty;
        private string lastFeedbackUnit = String.Empty;
        private float lastFeedbackBar = -1f;
        private bool forceFeedback = true;
        private DateTime lastAlertAt = DateTime.MinValue;
        private DateTime lastLayoutAttempt = DateTime.MinValue;
        private float? localValue = null;
        private string localValueBus = String.Empty;
        private int localValueChannelIndex = 0;
        private DateTime localValueAt = DateTime.MinValue;

        public OscDial(ISDConnection connection, InitialPayload payload) : base(connection, payload)
        {
            Connection.SetFeedbackLayoutAsync(LayoutPath);
            if (payload.Settings == null || payload.Settings.Count == 0)
            {
                this.settings = PluginSettings.CreateDefaultSettings();
                Connection.SetSettingsAsync(JObject.FromObject(settings));
            }
            else
            {
                this.settings = payload.Settings.ToObject<PluginSettings>();
                if (!payload.Settings.ContainsKey("DisplayChannelName"))
                {
                    this.settings.DisplayChannelName = true;
                    Connection.SetSettingsAsync(JObject.FromObject(settings));
                }
                if (!payload.Settings.ContainsKey("ChannelCount"))
                {
                    this.settings.ChannelCount = Globals.channelCount;
                    Connection.SetSettingsAsync(JObject.FromObject(settings));
                }
                if (!payload.Settings.ContainsKey("DialStep"))
                {
                    this.settings.DialStep = DefaultStep;
                    Connection.SetSettingsAsync(JObject.FromObject(settings));
                }
                if (this.settings.ChannelCount != Globals.channelCount)
                {
                    this.settings.ChannelCount = Globals.channelCount;
                    Connection.SetSettingsAsync(JObject.FromObject(settings));
                }

                if (!IsValidStep(this.settings.DialStep))
                {
                    this.settings.DialStep = DefaultStep;
                    Connection.SetSettingsAsync(JObject.FromObject(settings));
                }
            }
        }

        public override void DialRotate(DialRotatePayload payload)
        {
            EnsureFeedbackLayout();
            if (!Globals.commandConnection)
            {
                ShowAlertThrottled();
                return;
            }

            if (!TryResolveTarget(out string bus, out int channelIndex, out string volumeAddress))
            {
                ShowAlertThrottled();
                return;
            }

            if (!Globals.mirroringRequested || !Globals.backgroundConnection)
            {
                ShowAlertThrottled();
                return;
            }

            if (!Globals.bankSettings.TryGetValue(bus, out var busSettings) || !busSettings.TryGetValue(volumeAddress, out string currentValueRaw))
            {
                ShowAlertThrottled();
                return;
            }

            if (!TryParseFloat(currentValueRaw, out float currentValue))
            {
                ShowAlertThrottled();
                return;
            }

            float step = GetDialStep() * payload.Ticks;
            if (payload.IsDialPressed)
            {
                step *= 0.5f;
            }

            float newValue = Clamp(currentValue + step, 0f, 1f);

            Sender.Send($"/1/bus{bus}", 1, Globals.interfaceIp, Globals.interfacePort);
            Sender.Send(volumeAddress, newValue, Globals.interfaceIp, Globals.interfacePort);
            busSettings[volumeAddress] = newValue.ToString(CultureInfo.InvariantCulture);
            SetLocalValue(bus, channelIndex, newValue);

            UpdateFeedbackState(bus, channelIndex, volumeAddress, newValue, busSettings, preferComputedValue: true);
        }

        public override void Dispose()
        {
        }

        public override void DialDown(DialPayload payload)
        {
            EnsureFeedbackLayout();
        }

        public override void DialUp(DialPayload payload)
        {
            EnsureFeedbackLayout();
            if (!Globals.commandConnection)
            {
                ShowAlertThrottled();
                return;
            }

            if (!TryResolveTarget(out string bus, out int channelIndex, out string volumeAddress))
            {
                ShowAlertThrottled();
                return;
            }

            Sender.Send($"/1/bus{bus}", 1, Globals.interfaceIp, Globals.interfacePort);

            Sender.Send(volumeAddress, UnityFaderValue, Globals.interfaceIp, Globals.interfacePort);
            if (Globals.mirroringRequested && Globals.backgroundConnection && Globals.bankSettings.TryGetValue(bus, out var busSettings))
            {
                busSettings[volumeAddress] = UnityFaderValue.ToString(CultureInfo.InvariantCulture);
                SetLocalValue(bus, channelIndex, UnityFaderValue);
                UpdateFeedbackState(bus, channelIndex, volumeAddress, UnityFaderValue, busSettings, preferComputedValue: true);
            }
        }

        public override void TouchPress(TouchpadPressPayload payload)
        {
            EnsureFeedbackLayout();

            if (payload?.IsLongPress == true)
            {
                return;
            }

            if (!Globals.commandConnection)
            {
                ShowAlertThrottled();
                return;
            }

            if (!TryResolveTarget(out string bus, out int channelIndex, out string volumeAddress))
            {
                ShowAlertThrottled();
                return;
            }

            string muteAddress = $"/1/mute/1/{channelIndex}";
            if (Globals.mirroringRequested && Globals.backgroundConnection && Globals.bankSettings.TryGetValue(bus, out var muteSettings) && muteSettings.TryGetValue(muteAddress, out string muteValue))
            {
                bool isMuted = muteValue == "1";
                Sender.Send($"/1/bus{bus}", 1, Globals.interfaceIp, Globals.interfacePort);
                Sender.Send(muteAddress, isMuted ? 0 : 1, Globals.interfaceIp, Globals.interfacePort);
                muteSettings[muteAddress] = isMuted ? "0" : "1";
                UpdateFeedbackState(bus, channelIndex, volumeAddress, null, muteSettings);
            }
            else
            {
                ShowAlertThrottled();
            }
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            Tools.AutoPopulateSettings(settings, payload.Settings);
            EnsureFeedbackLayout();
        }

        public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload)
        {
        }

        public override void OnTick()
        {
            EnsureFeedbackLayout();
            if (!Globals.commandConnection)
            {
                UpdateFeedback("No connection", String.Empty, String.Empty, String.Empty, 0f);
                return;
            }

            if (!Globals.mirroringRequested || !Globals.backgroundConnection)
            {
                UpdateFeedback("Mirror off", String.Empty, String.Empty, String.Empty, 0f);
                return;
            }

            if (!TryResolveTarget(out string bus, out int channelIndex, out string volumeAddress))
            {
                UpdateFeedback("No channel", String.Empty, String.Empty, String.Empty, 0f);
                return;
            }

            if (!Globals.bankSettings.TryGetValue(bus, out var busSettings))
            {
                UpdateFeedback("No data", String.Empty, String.Empty, String.Empty, 0f);
                return;
            }

            string channelTitle = GetChannelTitle(channelIndex, busSettings);

            float? currentValue = null;
            if (busSettings.TryGetValue(volumeAddress, out string currentValueRaw) && TryParseFloat(currentValueRaw, out float parsedValue))
            {
                currentValue = parsedValue;
            }

            float displayValue = currentValue ?? 0f;
            bool preferComputedValue = false;
            if (TryGetDisplayValue(bus, channelIndex, currentValue, out float chosenValue, out bool preferComputed))
            {
                displayValue = chosenValue;
                preferComputedValue = preferComputed;
            }

            UpdateFeedbackState(bus, channelIndex, volumeAddress, displayValue, busSettings, channelTitle, preferComputedValue);
        }

        private bool TryResolveTarget(out string bus, out int channelIndex, out string volumeAddress)
        {
            bus = settings.Bus;
            channelIndex = 1;
            volumeAddress = settings.Name;

            if (!Int32.TryParse(settings.SelectedAction, out int selectedAction))
            {
                return false;
            }

            int channelCount = settings.ChannelCount > 0 ? settings.ChannelCount : Globals.channelCount;
            if (channelCount <= 0)
            {
                return false;
            }

            if (selectedAction <= channelCount)
            {
                bus = "Input";
                channelIndex = selectedAction;
            }
            else if (selectedAction > channelCount && selectedAction <= channelCount * 2)
            {
                bus = "Playback";
                channelIndex = selectedAction - channelCount;
            }
            else
            {
                bus = "Output";
                channelIndex = selectedAction - (channelCount * 2);
            }

            volumeAddress = $"/1/volume{channelIndex}";

            if (settings.Bus != bus || settings.Name != volumeAddress)
            {
                settings.Bus = bus;
                settings.Name = volumeAddress;
                Connection.SetSettingsAsync(JObject.FromObject(settings));
            }

            return true;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static bool TryParseFloat(string raw, out float value)
        {
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            return float.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private void UpdateFeedbackState(string bus, int channelIndex, string volumeAddress, float? barValue, Dictionary<string, string> busSettings, string channelTitleOverride = null, bool preferComputedValue = false)
        {
            string channelTitle = channelTitleOverride ?? GetChannelTitle(channelIndex, busSettings);

            float value = barValue ?? 0f;
            if (barValue == null && busSettings != null && busSettings.TryGetValue(volumeAddress, out string currentValueRaw) && TryParseFloat(currentValueRaw, out float parsedValue))
            {
                value = parsedValue;
            }

            var displayText = BuildValueText(busSettings, volumeAddress, value, preferComputedValue);
            string muteText = GetMuteText(busSettings, channelIndex);
            UpdateFeedback(channelTitle, muteText, displayText.value, displayText.unit, value);
        }

        private (string value, string unit) BuildValueText(Dictionary<string, string> busSettings, string volumeAddress, float value, bool preferComputedValue)
        {
            string valueText = String.Empty;
            if (!preferComputedValue && busSettings != null && busSettings.TryGetValue($"{volumeAddress}Val", out string oscValue))
            {
                valueText = oscValue;
            }
            if (String.IsNullOrWhiteSpace(valueText))
            {
                valueText = FormatFaderValue(value);
            }

            return SplitValueUnit(valueText);
        }

        private string GetChannelTitle(int channelIndex, Dictionary<string, string> busSettings)
        {
            if (!settings.DisplayChannelName)
            {
                return String.Empty;
            }

            if (busSettings != null && busSettings.TryGetValue($"/1/trackname{channelIndex}", out string trackname) && !String.IsNullOrWhiteSpace(trackname))
            {
                return trackname;
            }

            return $"Ch {channelIndex}";
        }

        private static string FormatFaderValue(float value)
        {
            if (value <= 0f)
            {
                return "-oo";
            }

            float faderPos = value * 1023f;
            float dB;
            if (faderPos >= 649f)
            {
                dB = faderPos * 0.0320855615f - 26.8235294118f;
            }
            else
            {
                dB = (faderPos * faderPos) * (-1f / 11033f) + faderPos * 0.1497326203f - 65f;
            }

            if (Math.Abs(dB) < 0.05f)
            {
                dB = 0f;
            }

            return $"{dB:0.0} dB";
        }

        private void UpdateFeedback(string channelTitle, string muteText, string valueText, string unitText, float barValue)
        {
            float clampedBar = Clamp(barValue, 0f, 1f);
            if (channelTitle == null)
            {
                channelTitle = String.Empty;
            }
            if (muteText == null)
            {
                muteText = String.Empty;
            }
            if (valueText == null)
            {
                valueText = String.Empty;
            }
            if (unitText == null)
            {
                unitText = String.Empty;
            }

            if (!forceFeedback &&
                String.Equals(lastFeedbackChannel, channelTitle, StringComparison.Ordinal) &&
                String.Equals(lastFeedbackMute, muteText, StringComparison.Ordinal) &&
                String.Equals(lastFeedbackValue, valueText, StringComparison.Ordinal) &&
                String.Equals(lastFeedbackUnit, unitText, StringComparison.Ordinal) &&
                Math.Abs(lastFeedbackBar - clampedBar) < 0.001f)
            {
                return;
            }

            lastFeedbackChannel = channelTitle;
            lastFeedbackMute = muteText;
            lastFeedbackValue = valueText;
            lastFeedbackUnit = unitText;
            lastFeedbackBar = clampedBar;

            var payload = new JObject
            {
                ["channel"] = channelTitle,
                ["mute"] = muteText,
                ["value"] = valueText,
                ["unit"] = unitText,
                ["bar"] = clampedBar
            };

            Connection.SetFeedbackAsync(payload);
            forceFeedback = false;
        }

        private void EnsureFeedbackLayout()
        {
            // Stream Deck sometimes ignores layout set too early; retry occasionally.
            if ((DateTime.UtcNow - lastLayoutAttempt).TotalSeconds < 2)
            {
                return;
            }

            lastLayoutAttempt = DateTime.UtcNow;
            Connection.SetFeedbackLayoutAsync(LayoutPath);
            forceFeedback = true;
        }

        private void ShowAlertThrottled()
        {
            if ((DateTime.UtcNow - lastAlertAt).TotalSeconds < 1.5)
            {
                return;
            }

            lastAlertAt = DateTime.UtcNow;
            Connection.ShowAlert();
        }

        private float GetDialStep()
        {
            float step = settings?.DialStep > 0f ? settings.DialStep : DefaultStep;
            if (!IsValidStep(step))
            {
                step = DefaultStep;
            }

            return Clamp(step, 0.001f, 1.0f);
        }

        private static bool IsValidStep(float step)
        {
            return !float.IsNaN(step) && step > 0f && step <= 1.0f;
        }

        private void SetLocalValue(string bus, int channelIndex, float value)
        {
            localValue = value;
            localValueBus = bus;
            localValueChannelIndex = channelIndex;
            localValueAt = DateTime.UtcNow;
        }

        private bool TryGetDisplayValue(string bus, int channelIndex, float? actualValue, out float displayValue, out bool preferComputedValue)
        {
            preferComputedValue = false;
            displayValue = actualValue ?? 0f;

            if (!localValue.HasValue || localValueBus != bus || localValueChannelIndex != channelIndex)
            {
                return actualValue.HasValue;
            }

            float local = localValue.Value;
            double ageMs = (DateTime.UtcNow - localValueAt).TotalMilliseconds;
            if (ageMs >= LocalValueMaxMs)
            {
                ClearLocalValue();
                displayValue = actualValue ?? local;
                return true;
            }

            preferComputedValue = true;
            if (actualValue.HasValue && Math.Abs(actualValue.Value - local) <= LocalValueEpsilon)
            {
                displayValue = actualValue.Value;
            }
            else
            {
                displayValue = local;
            }

            return true;
        }

        private void ClearLocalValue()
        {
            localValue = null;
            localValueBus = String.Empty;
            localValueChannelIndex = 0;
            localValueAt = DateTime.MinValue;
        }

        private static (string value, string unit) SplitValueUnit(string raw)
        {
            if (String.IsNullOrWhiteSpace(raw))
            {
                return (String.Empty, String.Empty);
            }

            string trimmed = raw.Trim();
            if (trimmed.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
            {
                string valuePart = trimmed.Substring(0, trimmed.Length - 2).TrimEnd();
                return (valuePart, "dB");
            }

            if (trimmed == "-oo")
            {
                return (trimmed, "dB");
            }

            return (trimmed, String.Empty);
        }

        private static string GetMuteText(Dictionary<string, string> busSettings, int channelIndex)
        {
            if (busSettings != null && busSettings.TryGetValue($"/1/mute/1/{channelIndex}", out string muteValue) && muteValue == "1")
            {
                return "M";
            }

            return String.Empty;
        }
    }
}
