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
        private const string DialFunctionVolume = "volume";
        private const string DialFunctionPan = "pan";
        private const string TouchActionMute = "mute";
        private const string TouchActionSolo = "solo";
        private const string TouchActionNone = "none";
        private const float DefaultStep = 0.02f;
        // Slightly above 0 dB on TotalMix's 0..1 scale to avoid "-0.0 dB" display
        private const float UnityFaderValue = 836.5f / 1023f;
        private const float CenterPanValue = 0.5f;
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
                    DialStep = DefaultStep,
                    DialFunction = DialFunctionVolume,
                    TouchAction = TouchActionMute
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

            [JsonProperty(PropertyName = "DialFunction")]
            public string DialFunction { get; set; }

            [JsonProperty(PropertyName = "TouchAction")]
            public string TouchAction { get; set; }

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
        private string localValueAddress = String.Empty;
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
                if (!payload.Settings.ContainsKey("DialFunction"))
                {
                    this.settings.DialFunction = DialFunctionVolume;
                    Connection.SetSettingsAsync(JObject.FromObject(settings));
                }
                if (!payload.Settings.ContainsKey("TouchAction"))
                {
                    this.settings.TouchAction = TouchActionMute;
                    Connection.SetSettingsAsync(JObject.FromObject(settings));
                }
                if (this.settings.ChannelCount != Globals.channelCount)
                {
                    this.settings.ChannelCount = Globals.channelCount;
                    Connection.SetSettingsAsync(JObject.FromObject(settings));
                }

                string normalizedFunction = NormalizeDialFunction(this.settings.DialFunction);
                if (this.settings.DialFunction != normalizedFunction)
                {
                    this.settings.DialFunction = normalizedFunction;
                    Connection.SetSettingsAsync(JObject.FromObject(settings));
                }
                string normalizedTouchAction = NormalizeTouchAction(this.settings.TouchAction);
                if (this.settings.TouchAction != normalizedTouchAction)
                {
                    this.settings.TouchAction = normalizedTouchAction;
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

            if (!TryResolveTarget(out string bus, out int channelIndex, out string parameterAddress, out string dialFunction))
            {
                ShowAlertThrottled();
                return;
            }

            if (!Globals.mirroringRequested || !Globals.backgroundConnection)
            {
                ShowAlertThrottled();
                return;
            }

            if (!Globals.bankSettings.TryGetValue(bus, out var busSettings) || !busSettings.TryGetValue(parameterAddress, out string currentValueRaw))
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
            Sender.Send(parameterAddress, newValue, Globals.interfaceIp, Globals.interfacePort);
            busSettings[parameterAddress] = newValue.ToString(CultureInfo.InvariantCulture);
            SetLocalValue(bus, channelIndex, parameterAddress, newValue);

            UpdateFeedbackState(bus, channelIndex, parameterAddress, newValue, busSettings, preferComputedValue: true);
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

            if (!TryResolveTarget(out string bus, out int channelIndex, out string parameterAddress, out string dialFunction))
            {
                ShowAlertThrottled();
                return;
            }

            Sender.Send($"/1/bus{bus}", 1, Globals.interfaceIp, Globals.interfacePort);

            float defaultValue = dialFunction == DialFunctionPan ? CenterPanValue : UnityFaderValue;
            Sender.Send(parameterAddress, defaultValue, Globals.interfaceIp, Globals.interfacePort);
            if (Globals.mirroringRequested && Globals.backgroundConnection && Globals.bankSettings.TryGetValue(bus, out var busSettings))
            {
                busSettings[parameterAddress] = defaultValue.ToString(CultureInfo.InvariantCulture);
                SetLocalValue(bus, channelIndex, parameterAddress, defaultValue);
                UpdateFeedbackState(bus, channelIndex, parameterAddress, defaultValue, busSettings, preferComputedValue: true);
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

            if (!TryResolveTarget(out string bus, out int channelIndex, out string parameterAddress, out string dialFunction))
            {
                ShowAlertThrottled();
                return;
            }

            string toggleAddress = GetTouchAddress(bus, channelIndex);
            if (String.IsNullOrEmpty(toggleAddress))
            {
                return;
            }
            if (Globals.mirroringRequested && Globals.backgroundConnection && Globals.bankSettings.TryGetValue(bus, out var toggleSettings) && toggleSettings.TryGetValue(toggleAddress, out string toggleValue))
            {
                bool isEnabled = toggleValue == "1";
                Sender.Send($"/1/bus{bus}", 1, Globals.interfaceIp, Globals.interfacePort);
                Sender.Send(toggleAddress, isEnabled ? 0 : 1, Globals.interfaceIp, Globals.interfacePort);
                toggleSettings[toggleAddress] = isEnabled ? "0" : "1";
                UpdateFeedbackState(bus, channelIndex, parameterAddress, null, toggleSettings);
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

            if (!TryResolveTarget(out string bus, out int channelIndex, out string parameterAddress, out _))
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
            if (busSettings.TryGetValue(parameterAddress, out string currentValueRaw) && TryParseFloat(currentValueRaw, out float parsedValue))
            {
                currentValue = parsedValue;
            }

            float displayValue = currentValue ?? 0f;
            bool preferComputedValue = false;
            if (TryGetDisplayValue(bus, channelIndex, parameterAddress, currentValue, out float chosenValue, out bool preferComputed))
            {
                displayValue = chosenValue;
                preferComputedValue = preferComputed;
            }

            UpdateFeedbackState(bus, channelIndex, parameterAddress, displayValue, busSettings, channelTitle, preferComputedValue);
        }

        private bool TryResolveTarget(out string bus, out int channelIndex, out string parameterAddress, out string dialFunction)
        {
            bus = settings.Bus;
            channelIndex = 1;
            parameterAddress = settings.Name;
            dialFunction = NormalizeDialFunction(settings.DialFunction);

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

            string addressSuffix = dialFunction == DialFunctionPan ? "pan" : "volume";
            parameterAddress = $"/1/{addressSuffix}{channelIndex}";

            if (settings.Bus != bus || settings.Name != parameterAddress || settings.DialFunction != dialFunction)
            {
                settings.Bus = bus;
                settings.Name = parameterAddress;
                settings.DialFunction = dialFunction;
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

        private void UpdateFeedbackState(string bus, int channelIndex, string parameterAddress, float? barValue, Dictionary<string, string> busSettings, string channelTitleOverride = null, bool preferComputedValue = false)
        {
            string channelTitle = channelTitleOverride ?? GetChannelTitle(channelIndex, busSettings);

            float value = barValue ?? 0f;
            if (barValue == null && busSettings != null && busSettings.TryGetValue(parameterAddress, out string currentValueRaw) && TryParseFloat(currentValueRaw, out float parsedValue))
            {
                value = parsedValue;
            }

            var displayText = BuildValueText(busSettings, parameterAddress, value, preferComputedValue);
            string toggleText = GetToggleText(bus, busSettings, channelIndex);
            UpdateFeedback(channelTitle, toggleText, displayText.value, displayText.unit, value);
        }

        private (string value, string unit) BuildValueText(Dictionary<string, string> busSettings, string parameterAddress, float value, bool preferComputedValue)
        {
            string valueText = String.Empty;
            if (!preferComputedValue && busSettings != null && busSettings.TryGetValue($"{parameterAddress}Val", out string oscValue))
            {
                valueText = oscValue;
            }
            if (String.IsNullOrWhiteSpace(valueText))
            {
                valueText = IsPanFunction() ? FormatPanValue(value) : FormatFaderValue(value);
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

        private static string FormatPanValue(float value)
        {
            float clamped = Clamp(value, 0f, 1f);
            int pan = (int)Math.Round((clamped - 0.5f) * 200f);
            if (Math.Abs(pan) <= 0)
            {
                return "C";
            }

            if (pan < 0)
            {
                return $"L {Math.Abs(pan)}";
            }

            return $"R {pan}";
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

        private void SetLocalValue(string bus, int channelIndex, string parameterAddress, float value)
        {
            localValue = value;
            localValueBus = bus;
            localValueChannelIndex = channelIndex;
            localValueAddress = parameterAddress;
            localValueAt = DateTime.UtcNow;
        }

        private bool TryGetDisplayValue(string bus, int channelIndex, string parameterAddress, float? actualValue, out float displayValue, out bool preferComputedValue)
        {
            preferComputedValue = false;
            displayValue = actualValue ?? 0f;

            if (!localValue.HasValue || localValueBus != bus || localValueChannelIndex != channelIndex || localValueAddress != parameterAddress)
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
            localValueAddress = String.Empty;
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

        private string GetToggleText(string bus, Dictionary<string, string> busSettings, int channelIndex)
        {
            string touchAction = NormalizeTouchAction(settings.TouchAction);
            if (touchAction == TouchActionNone)
            {
                return String.Empty;
            }
            if (touchAction == TouchActionSolo && !IsSoloSupported(bus))
            {
                return String.Empty;
            }
            string address = touchAction == TouchActionSolo ? $"/1/solo/1/{channelIndex}" : $"/1/mute/1/{channelIndex}";
            if (busSettings != null && busSettings.TryGetValue(address, out string toggleValue) && toggleValue == "1")
            {
                return touchAction == TouchActionSolo ? "S" : "M";
            }

            return String.Empty;
        }

        private string GetTouchAddress(string bus, int channelIndex)
        {
            string touchAction = NormalizeTouchAction(settings.TouchAction);
            if (touchAction == TouchActionNone)
            {
                return String.Empty;
            }
            if (touchAction == TouchActionSolo && !IsSoloSupported(bus))
            {
                return String.Empty;
            }
            return touchAction == TouchActionSolo
                ? $"/1/solo/1/{channelIndex}"
                : $"/1/mute/1/{channelIndex}";
        }

        private static string NormalizeDialFunction(string value)
        {
            if (String.Equals(value, DialFunctionPan, StringComparison.OrdinalIgnoreCase))
            {
                return DialFunctionPan;
            }

            return DialFunctionVolume;
        }

        private static string NormalizeTouchAction(string value)
        {
            if (String.Equals(value, TouchActionSolo, StringComparison.OrdinalIgnoreCase))
            {
                return TouchActionSolo;
            }

            if (String.Equals(value, TouchActionNone, StringComparison.OrdinalIgnoreCase))
            {
                return TouchActionNone;
            }

            return TouchActionMute;
        }

        private bool IsPanFunction()
        {
            return NormalizeDialFunction(settings.DialFunction) == DialFunctionPan;
        }

        private static bool IsSoloSupported(string bus)
        {
            return !String.Equals(bus, "Output", StringComparison.OrdinalIgnoreCase);
        }
    }
}
