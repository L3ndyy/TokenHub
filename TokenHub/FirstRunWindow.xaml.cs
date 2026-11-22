using System.Windows;
using TokenHub.Services;

namespace TokenHub;

/// <summary>
/// First-run wizard. A freshly installed OpenCode has no
/// <c>~/.config/opencode</c>, so the app fills it in instead of sending the user
/// to edit JSON by hand (item requested by the user).
/// </summary>
public partial class FirstRunWindow : Window
{
    /// <summary>True when the user finished the wizard, false when they chose "Позже".</summary>
    public bool Applied { get; private set; }

    /// <summary>How many skill folders were copied during this run.</summary>
    public int SkillsInstalled { get; private set; }

    public FirstRunWindow()
    {
        InitializeComponent();

        int skills = Skills.Catalog.Count;
        TglSkills.IsChecked = skills > 0;
        TglSkills.IsEnabled = skills > 0;
        TxtSkillsTitle.Text = skills > 0
            ? $"Установить скиллы ({skills})"
            : "Скиллы недоступны";
        TxtSkillsDetail.Text = skills > 0
            ? $"Скопировать в {Skills.Folder}. Уже установленные не пострадают."
            : "В этой сборке нет набора скиллов.";

        TxtPlan.Text =
            $"OpenCode:   {OpenCodeConfig.FilePath}{Environment.NewLine}" +
            $"Скиллы:     {Skills.Folder}{Environment.NewLine}" +
            $"Пул ключей: {App.Settings.LocalBaseUrl}{Environment.NewLine}{Environment.NewLine}" +
            "TokenHub подключит OpenCode к локальному пулу, и ключи будут переключаться без перезапуска.";
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        // Remember the "no", so the wizard does not open on every launch.
        App.Settings.FirstRunDone = true;
        SettingsStore.Save(App.Settings);
        Applied = false;
        DialogResult = false;
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        string key = TxtKey.Text.Trim();

        if (key.Length > 0 && !key.StartsWith("thk_", StringComparison.OrdinalIgnoreCase))
        {
            TxtStatus.Text = "Ключ должен начинаться с thk_ — скопируйте его из личного кабинета tokenharbor.ai.";
            TxtKey.Focus();
            return;
        }

        if (key.Length == 0 && App.Settings.Keys.Count == 0)
        {
            TxtStatus.Text = "Без ключа OpenCode не сможет работать. Вставьте ключ или нажмите «Позже».";
            TxtKey.Focus();
            return;
        }

        try
        {
            if (key.Length > 0 && App.Settings.Keys.All(k => k.Key != key))
            {
                App.Settings.Keys.Add(new ApiKeyEntry
                {
                    Key = key,
                    Label = $"Ключ {App.Settings.Keys.Count + 1}",
                });
            }

            App.Settings.UsePool = true;

            if (TglSkills.IsChecked == true)
            {
                SkillsInstalled = Skills.InstallAll();
            }

            App.Settings.FirstRunDone = true;
            SettingsStore.Save(App.Settings);
            EventLog.Write($"Первый запуск: ключей {App.Settings.Keys.Count}, скиллов {SkillsInstalled}");

            Applied = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Не удалось сохранить настройки: " + ex.Message;
        }
    }
}