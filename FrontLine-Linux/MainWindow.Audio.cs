using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FrontLine_Linux;

public partial class MainWindow
{
    private sealed record AudioOutputOption(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private string? _audioOutput;
    private AudioOutputOption[] _audioOutputs = Array.Empty<AudioOutputOption>();
    private bool _refreshingAudioOutputs;
    private bool _audioOutputsFailed;

    private string AudioText(string en, string pt, string es, string id) => DialogUi.L(_appLanguage, en, pt, es, id);

    private static AudioOutputOption[] ReadAudioOutputs(JsonElement outputs)
    {
        var devices = new List<AudioOutputOption>();
        if (outputs.ValueKind != JsonValueKind.Array) return devices.ToArray();
        foreach (var output in outputs.EnumerateArray())
        {
            if (output.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(output, "id");
            if (!string.IsNullOrWhiteSpace(id))
                devices.Add(new AudioOutputOption(id, ReadString(output, "label") ?? id));
        }
        return devices.ToArray();
    }

    private async Task RestoreAudioOutputAsync()
    {
        await SendAsync(new { action = "AUDIO_SELECT_OUTPUT", output = _audioOutput });
        await SendAsync(new { action = "AUDIO_OUTPUTS" });
    }

    private void ApplyAudioOptionsLanguage()
    {
        if (AudioOutputCombo is null) return;
        AudioOutputLabel.Text = AudioText("Audio output for recognition", "Saída de áudio para reconhecimento",
            "Salida de audio para el reconocimiento", "Keluaran audio untuk pengenalan");
        AudioOutputRefreshButton.Content = AudioText("Refresh outputs", "Atualizar saídas", "Actualizar salidas", "Perbarui keluaran");
        AudioOutputRefreshButton.IsEnabled = AudioOutputCombo.IsEnabled = _connected;
        AudioOutputHint.Text = AudioText("Changes apply to the next capture. Select Listen to check the signal.",
            "A escolha vale na próxima captura. Clique em Ouvir para conferir o sinal.",
            "La selección se aplica a la próxima captura. Pulsa Escuchar para comprobar la señal.",
            "Pilihan berlaku pada perekaman berikutnya. Pilih Dengarkan untuk memeriksa sinyal.");
        SettingsAudioLevelLabel.Text =
            AudioText("Audio signal", "Sinal de áudio", "Señal de audio", "Sinyal audio");
        AudioOutputError.Text = AudioText("Unable to list audio outputs.", "Não foi possível listar as saídas de áudio.",
            "No se pudieron listar las salidas de audio.", "Daftar keluaran audio tidak tersedia.");
        RefreshAudioOutputChoices();
    }

    private void UpdateAudioOutputs(AudioOutputOption[] devices, bool failed)
    {
        if (!_connected) return;
        _audioOutputsFailed = failed;
        if (!failed) _audioOutputs = devices;
        RefreshAudioOutputChoices();
    }

    private void RefreshAudioOutputChoices()
    {
        var choices = new List<AudioOutputOption>
        {
            new(null, AudioText("System default", "Saída padrão do sistema", "Salida predeterminada", "Keluaran bawaan sistem"))
        };
        choices.AddRange(_audioOutputs.OrderBy(device => device.Label));
        var selected = choices.Find(choice => choice.Id == _audioOutput);
        if (selected is null)
        {
            var missing = AudioText("Unavailable", "Indisponível", "No disponible", "Tidak tersedia");
            selected = new AudioOutputOption(_audioOutput, $"{missing} · {_audioOutput}");
            choices.Add(selected);
        }

        _refreshingAudioOutputs = true;
        try
        {
            AudioOutputCombo.ItemsSource = choices;
            AudioOutputCombo.SelectedItem = selected;
            AudioOutputError.IsVisible = _connected && _audioOutputsFailed;
        }
        finally { _refreshingAudioOutputs = false; }
    }

    private async void AudioOutput_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingAudioOutputs || AudioOutputCombo?.SelectedItem is not AudioOutputOption option
            || option.Id == _audioOutput) return;
        _audioOutput = option.Id;
        SavePreferences();
        await SendAsync(new { action = "AUDIO_SELECT_OUTPUT", output = _audioOutput });
    }

    private async void AudioOutputRefresh_Click(object? sender, RoutedEventArgs e) =>
        await SendAsync(new { action = "AUDIO_OUTPUTS" });

    private void UpdateAudioMeter(bool active, double level)
    {
        var value = active && double.IsFinite(level) ? Math.Clamp(level, 0, 1) * 100 : 0;
        SettingsAudioLevel.Value = value;
    }
}
