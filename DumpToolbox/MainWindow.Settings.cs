using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace DumpToolbox;

public partial class MainWindow
{
    private IniSettingsStore? _userSettings;
    private bool _loadingUserSettings;
    private bool _settingsWindowOpened;
    private bool _suppressWindowPositionPersistence;
    private bool _updatingMenuLayoutSelection;

    private void InitializeUserSettings()
    {
        bool writeInitialDefaults = false;
        try
        {
            _loadingUserSettings = true;
            _userSettings = IniSettingsStore.Open();
            writeInitialDefaults = _userSettings.CreatedOnOpen;
            LoadUserSettings();
        }
        catch
        {
            // Settings must never stop DumpToolbox from starting.  A read-only
            // or malformed environment simply behaves as if persistence were
            // unavailable for this run.
            _userSettings = null;
        }
        finally
        {
            _loadingUserSettings = false;
        }

        // First run creates a real, useful INI immediately rather than an empty
        // placeholder. The file is generated at runtime only.
        if (writeInitialDefaults)
            SaveUserSettings();

        Opened += (_, _) =>
        {
            _settingsWindowOpened = true;
            SaveUserSettings();
        };
        PositionChanged += (_, _) =>
        {
            if (_settingsWindowOpened && !_loadingUserSettings)
                _suppressWindowPositionPersistence = false;
        };
        Closing += (_, _) => SaveUserSettings();
        Deactivated += (_, _) => SaveUserSettings();
    }

    private void LoadUserSettings()
    {
        if (_userSettings is null)
            return;

        // Window / navigation.
        double width = _userSettings.GetDouble("Window", "Width", 1060);
        double height = _userSettings.GetDouble("Window", "Height", 780);
        if (double.IsFinite(width) && double.IsFinite(height) && width >= MinWidth && height >= MinHeight)
        {
            Width = width;
            Height = height;
            _lastNormalClientSize = new Size(width, height);
            _normalSizeInitialized = true;
        }

        int x = _userSettings.GetInt("Window", "X", int.MinValue);
        int y = _userSettings.GetInt("Window", "Y", int.MinValue);
        if (x != int.MinValue && y != int.MinValue && x is > -20000 and < 20000 && y is > -20000 and < 20000)
            Position = new PixelPoint(x, y);

        string state = _userSettings.Get("Window", "State", "Normal");
        WindowState = state.Equals("Maximized", StringComparison.OrdinalIgnoreCase)
            ? WindowState.Maximized
            : WindowState.Normal;

        SetSelectedIndex(MainTabControl, _userSettings.GetInt("Navigation", "MainTab", 0));
        SetSelectedIndex(ConvertTabControl, _userSettings.GetInt("Navigation", "ConvertTab", 0));
        SetSelectedIndex(OtherToolsTabControl, _userSettings.GetInt("Navigation", "OtherToolsTab", 0));

        // Global settings.
        string theme = _userSettings.Get("Settings", "Theme", "System");
        SettingsCustomBackgroundPicker.Color = ParseThemeColor(_userSettings.Get("Settings", "CustomBackground", "#202020"), Color.Parse("#202020"));
        SettingsCustomTextPicker.Color = ParseThemeColor(_userSettings.Get("Settings", "CustomText", "#F2F2F2"), Color.Parse("#F2F2F2"));
        SettingsCustomAccentPicker.Color = ParseThemeColor(_userSettings.Get("Settings", "CustomAccent", "#0078D4"), Color.Parse("#0078D4"));
        SettingsCustomInputPicker.Color = ParseThemeColor(_userSettings.Get("Settings", "CustomInput", "#2B2B2B"), Color.Parse("#2B2B2B"));
        SettingsThemeSystemRadio.IsChecked = theme.Equals("System", StringComparison.OrdinalIgnoreCase);
        SettingsThemeLightRadio.IsChecked = theme.Equals("Light", StringComparison.OrdinalIgnoreCase);
        SettingsThemeDarkRadio.IsChecked = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        SettingsThemeCustomRadio.IsChecked = theme.Equals("Custom", StringComparison.OrdinalIgnoreCase);
        if (SettingsThemeSystemRadio.IsChecked != true && SettingsThemeLightRadio.IsChecked != true && SettingsThemeDarkRadio.IsChecked != true && SettingsThemeCustomRadio.IsChecked != true)
            SettingsThemeSystemRadio.IsChecked = true;
        SettingsCustomThemePanel.IsVisible = SettingsThemeCustomRadio.IsChecked == true;
        SettingsRememberPathsCheckBox.IsChecked = _userSettings.GetBool("Settings", "RememberLastUsedPaths", true);

        string menuLayout = _userSettings.Get("Settings", "MenuLayout", "Horizontal");
        bool verticalMenu = menuLayout.Equals("Vertical", StringComparison.OrdinalIgnoreCase);
        SettingsMenuHorizontalRadio.IsChecked = !verticalMenu;
        SettingsMenuVerticalRadio.IsChecked = verticalMenu;
        ApplyMainMenuLayout(verticalMenu);

        // v0.8.39: the old SkeleTool run-history preference is intentionally not
        // migrated. The replacement collection catalogue is independent from
        // reconstruction and is enabled by default.
        SettingsSha1DatabaseCheckBox.IsChecked =
            _userSettings.GetBool("Settings", "Sha1CatalogueEnabled", true);
        SettingsSha1ThreadsBox.Value = Math.Clamp(_userSettings.GetInt("Settings", "Sha1CatalogueThreads", Math.Min(4, Environment.ProcessorCount)), 1, 64);
        SettingsHeadsTailsThreadsBox.Value = Math.Clamp(_userSettings.GetInt("HeadsAndTails", "Threads", 4), 1, 64);
        ApplySelectedTheme();

        // FindCRCs.
        LoadRememberedPath(FilePathBox, "FindCRCs", "SourcePath");
        LoadRememberedPath(FindCrcsCueBox, "FindCRCs", "CuePath");
        FindCrcsEdgeRepairCheckBox.IsChecked = _userSettings.GetBool("FindCRCs", "EdgeRepair", false);
        FindCrcsPregapScrambleCheckBox.IsChecked = _userSettings.GetBool("FindCRCs", "PregapScramble", false);
        FindCrcsSavePartialCheckBox.IsChecked = _userSettings.GetBool("FindCRCs", "SavePartial", false);

        // Audio. Deliberately do not persist hash targets or the source-file
        // queue; only stable working preferences and paths are retained.
        LoadRememberedPath(AudioOutputFolderBox, "Audio", "OutputFolder");
        AudioEdgeSecondsBox.Text = _userSettings.Get("Audio", "EdgeSeconds", "5");
        AudioEdgeRepairCheckBox.IsChecked = _userSettings.GetBool("Audio", "EdgeRepair", false);
        AudioSavePartialCheckBox.IsChecked = _userSettings.GetBool("Audio", "SavePartial", false);
        AudioHeadsTailsCheckBox.IsChecked = _userSettings.GetBool("Audio", "HeadsTails", false);
        AudioDeleteTempCheckBox.IsChecked = _userSettings.GetBool("Audio", "DeleteWorkingFiles", false);

        // ISO2BIN.
        LoadRememberedPath(IsoInputBox, "ISO2BIN", "InputPath");
        LoadRememberedPath(IsoCueBox, "ISO2BIN", "CuePath");
        LoadRememberedPath(IsoXaMetadataBox, "ISO2BIN", "XaMetadataPath");
        LoadRememberedPath(IsoOutputBox, "ISO2BIN", "OutputPath");
        SetSelectedIndex(IsoModeBox, _userSettings.GetInt("ISO2BIN", "Mode", 0));
        IsoSendToFindCrcsCheckBox.IsChecked = _userSettings.GetBool("ISO2BIN", "SendToFindCRCs", false);

        // MDF2BIN.
        LoadRememberedPath(Mdf2BinMdsBox, "MDF2BIN", "MdsPath");
        LoadRememberedPath(Mdf2BinMdfBox, "MDF2BIN", "MdfPath");
        LoadRememberedPath(Mdf2BinOutputBinBox, "MDF2BIN", "OutputBinPath");
        LoadRememberedPath(Mdf2BinOutputCueBox, "MDF2BIN", "OutputCuePath");
        Mdf2BinSaveSubCheckBox.IsChecked = _userSettings.GetBool("MDF2BIN", "SaveSubchannel", false);
        Mdf2BinSendToFindCrcsCheckBox.IsChecked = _userSettings.GetBool("MDF2BIN", "SendToFindCRCs", false);

        // NRG2BIN.
        LoadRememberedPath(Nrg2BinInputBox, "NRG2BIN", "InputPath");
        LoadRememberedPath(Nrg2BinOutputBinBox, "NRG2BIN", "OutputBinPath");
        LoadRememberedPath(Nrg2BinOutputCueBox, "NRG2BIN", "OutputCuePath");
        Nrg2BinSaveSubCheckBox.IsChecked = _userSettings.GetBool("NRG2BIN", "SaveSubchannel", false);
        Nrg2BinSendToFindCrcsCheckBox.IsChecked = _userSettings.GetBool("NRG2BIN", "SendToFindCRCs", false);

        // CDI2BIN.
        LoadRememberedPath(Cdi2BinInputBox, "CDI2BIN", "InputPath");
        LoadRememberedPath(Cdi2BinOutputBinBox, "CDI2BIN", "OutputBinPath");
        LoadRememberedPath(Cdi2BinOutputCueBox, "CDI2BIN", "OutputCuePath");
        Cdi2BinSaveSubCheckBox.IsChecked = _userSettings.GetBool("CDI2BIN", "SaveSubchannel", false);
        Cdi2BinSendToFindCrcsCheckBox.IsChecked = _userSettings.GetBool("CDI2BIN", "SendToFindCRCs", false);

        // SkeleTool.
        LoadRememberedPath(SkeletonPathBox, "SkeleTool", "SkeletonPath");
        LoadRememberedPath(SkeletonHashBox, "SkeleTool", "HashPath");
        LoadRememberedPath(SkeletonSourceFolderBox, "SkeleTool", "SourceFolder");
        LoadRememberedPath(SkeletonSourceImageBox, "SkeleTool", "SourceImage");
        LoadRememberedPath(SkeletonOutputBox, "SkeleTool", "OutputPath");
        SkeletonRecursiveCheckBox.IsChecked = _userSettings.GetBool("SkeleTool", "Recursive", true);
        SkeletonAllowMissingCheckBox.IsChecked = _userSettings.GetBool("SkeleTool", "AllowMissing", false);
        SkeletonForceRehashCheckBox.IsChecked = _userSettings.GetBool("SkeleTool", "ForceRehash", false);

        // DIC. Recovery-state/match data remains in its existing per-disc JSON;
        // the INI stores only UI inputs/preferences.
        LoadRememberedPath(DicLogPathBox, "DIC", "LogPath");
        LoadRememberedPath(DicSourceFolderBox, "DIC", "SourceFolder");
        LoadRememberedPath(DicDonorImageBox, "DIC", "DonorImagePath");
        LoadRememberedPath(DicOutputBox, "DIC", "OutputPath");
        DicAllowMissingCheckBox.IsChecked = _userSettings.GetBool("DIC", "AllowMissing", false);
        DicForceRehashCheckBox.IsChecked = _userSettings.GetBool("DIC", "ForceRehash", false);
        DicVerboseLoggingCheckBox.IsChecked = _userSettings.GetBool("DIC", "verbose",
            _userSettings.GetBool("General", "verbose", false));

        // IRD. The typed disc key is intentionally never persisted.
        LoadRememberedPath(IrdPathBox, "IRD", "IrdPath");
        LoadRememberedPath(IrdSourceFolderBox, "IRD", "SourceFolder");
        LoadRememberedPath(IrdOutputBox, "IRD", "OutputPath");
        LoadRememberedPath(IrdKeyFileBox, "IRD", "KeyFilePath");
        IrdKeyTextBox.Text = string.Empty;
        IrdEncryptCheckBox.IsChecked = _userSettings.GetBool("IRD", "EncryptOutput", false);

        // Concatenate.
        LoadRememberedPath(ConcatDestinationBox, "Concatenate", "DestinationPath");
        ConcatPaddingCheckBox.IsChecked = _userSettings.GetBool("Concatenate", "ZeroPadding", false);
        ConcatPaddingBytesBox.Text = _userSettings.Get("Concatenate", "PaddingBytes", "10240");
        ConcatBoundaryCheckBox.IsChecked = _userSettings.GetBool("Concatenate", "SkipUnsafeBoundaries", true);

        // Hash calculator.
        LoadRememberedPath(HashCalcFileBox, "HashCalc", "FilePath");
        HashCalcCrc32CheckBox.IsChecked = _userSettings.GetBool("HashCalc", "CRC32", true);
        HashCalcMd5CheckBox.IsChecked = _userSettings.GetBool("HashCalc", "MD5", true);
        HashCalcSha1CheckBox.IsChecked = _userSettings.GetBool("HashCalc", "SHA1", true);
        HashCalcSha256CheckBox.IsChecked = _userSettings.GetBool("HashCalc", "SHA256", false);
        HashCalcSha384CheckBox.IsChecked = _userSettings.GetBool("HashCalc", "SHA384", false);
        HashCalcSha512CheckBox.IsChecked = _userSettings.GetBool("HashCalc", "SHA512", false);

        // Base64. Text contents are intentionally not saved.
        SetSelectedIndex(Base64OperationBox, _userSettings.GetInt("Base64", "Operation", 0));
        SetSelectedIndex(Base64InputTypeBox, _userSettings.GetInt("Base64", "InputType", 0));
        LoadRememberedPath(Base64InputFileBox, "Base64", "InputFilePath");
        LoadRememberedPath(Base64OutputFileBox, "Base64", "OutputFilePath");

        // Find-Ends. The target/hash text is intentionally not persisted.
        LoadRememberedPath(FindEndsPartialBox, "FindEnds", "PartialPath");
        LoadRememberedPath(FindEndsSourceBox, "FindEnds", "SourcePath");
        LoadRememberedPath(FindEndsOutputBox, "FindEnds", "OutputPath");
        SetSelectedIndex(FindEndsModeBox, _userSettings.GetInt("FindEnds", "Mode", 0));

        // ISO Extractor.
        LoadRememberedPath(IsoExtractImagePathBox, "ISOExtractor", "ImagePath");
        LoadRememberedPath(IsoExtractOutputFolderBox, "ISOExtractor", "OutputFolder");
    }

    private void SaveUserSettings()
    {
        if (_loadingUserSettings || _userSettings is null)
            return;

        try
        {
            _userSettings.Set("General", "SettingsFormat", 1);
            _userSettings.Set("General", "ApplicationVersion", GetApplicationVersion());

            _userSettings.Set("Settings", "Theme", GetSelectedThemeName());
            _userSettings.Set("Settings", "CustomBackground", ThemeColorToString(SettingsCustomBackgroundPicker.Color));
            _userSettings.Set("Settings", "CustomText", ThemeColorToString(SettingsCustomTextPicker.Color));
            _userSettings.Set("Settings", "CustomAccent", ThemeColorToString(SettingsCustomAccentPicker.Color));
            _userSettings.Set("Settings", "CustomInput", ThemeColorToString(SettingsCustomInputPicker.Color));
            _userSettings.Set("Settings", "RememberLastUsedPaths", RememberLastUsedPaths);
            _userSettings.Set("Settings", "MenuLayout", SettingsMenuVerticalRadio.IsChecked == true ? "Vertical" : "Horizontal");
            _userSettings.Set("Settings", "Sha1CatalogueEnabled", IsSha1DatabaseEnabled);
            _userSettings.Set("Settings", "Sha1CatalogueThreads", (int)(SettingsSha1ThreadsBox.Value ?? 1));
            _userSettings.Set("HeadsAndTails", "Threads", Math.Clamp((int)(SettingsHeadsTailsThreadsBox.Value ?? 4), 1, 64));
            _userSettings.RemoveKey("Settings", "Sha1DatabaseEnabled");
            _userSettings.RemoveKey("SkeleTool", "UseHistoryDatabase");

            Size normal = _normalSizeInitialized ? _lastNormalClientSize : ClientSize;
            if (double.IsFinite(normal.Width) && normal.Width >= MinWidth)
                _userSettings.Set("Window", "Width", normal.Width);
            if (double.IsFinite(normal.Height) && normal.Height >= MinHeight)
                _userSettings.Set("Window", "Height", normal.Height);
            if (_settingsWindowOpened && !_suppressWindowPositionPersistence)
            {
                _userSettings.Set("Window", "X", Position.X);
                _userSettings.Set("Window", "Y", Position.Y);
            }
            _userSettings.Set("Window", "State", WindowState == WindowState.Maximized ? "Maximized" : "Normal");

            _userSettings.Set("Navigation", "MainTab", MainTabControl.SelectedIndex);
            _userSettings.Set("Navigation", "ConvertTab", ConvertTabControl.SelectedIndex);
            _userSettings.Set("Navigation", "OtherToolsTab", OtherToolsTabControl.SelectedIndex);

            SaveRememberedPath("FindCRCs", "SourcePath", FilePathBox.Text);
            SaveRememberedPath("FindCRCs", "CuePath", FindCrcsCueBox.Text);
            _userSettings.Set("FindCRCs", "EdgeRepair", Checked(FindCrcsEdgeRepairCheckBox));
            _userSettings.Set("FindCRCs", "PregapScramble", Checked(FindCrcsPregapScrambleCheckBox));
            _userSettings.Set("FindCRCs", "SavePartial", Checked(FindCrcsSavePartialCheckBox));

            SaveRememberedPath("Audio", "OutputFolder", AudioOutputFolderBox.Text);
            _userSettings.Set("Audio", "EdgeSeconds", Text(AudioEdgeSecondsBox.Text));
            _userSettings.Set("Audio", "EdgeRepair", Checked(AudioEdgeRepairCheckBox));
            _userSettings.Set("Audio", "SavePartial", Checked(AudioSavePartialCheckBox));
            _userSettings.Set("Audio", "HeadsTails", Checked(AudioHeadsTailsCheckBox));
            _userSettings.Set("Audio", "DeleteWorkingFiles", Checked(AudioDeleteTempCheckBox));

            SaveRememberedPath("ISO2BIN", "InputPath", IsoInputBox.Text);
            SaveRememberedPath("ISO2BIN", "CuePath", IsoCueBox.Text);
            SaveRememberedPath("ISO2BIN", "XaMetadataPath", IsoXaMetadataBox.Text);
            SaveRememberedPath("ISO2BIN", "OutputPath", IsoOutputBox.Text);
            _userSettings.Set("ISO2BIN", "Mode", IsoModeBox.SelectedIndex);
            _userSettings.Set("ISO2BIN", "SendToFindCRCs", Checked(IsoSendToFindCrcsCheckBox));

            SaveRememberedPath("MDF2BIN", "MdsPath", Mdf2BinMdsBox.Text);
            SaveRememberedPath("MDF2BIN", "MdfPath", Mdf2BinMdfBox.Text);
            SaveRememberedPath("MDF2BIN", "OutputBinPath", Mdf2BinOutputBinBox.Text);
            SaveRememberedPath("MDF2BIN", "OutputCuePath", Mdf2BinOutputCueBox.Text);
            _userSettings.Set("MDF2BIN", "SaveSubchannel", Checked(Mdf2BinSaveSubCheckBox));
            _userSettings.Set("MDF2BIN", "SendToFindCRCs", Checked(Mdf2BinSendToFindCrcsCheckBox));

            SaveRememberedPath("NRG2BIN", "InputPath", Nrg2BinInputBox.Text);
            SaveRememberedPath("NRG2BIN", "OutputBinPath", Nrg2BinOutputBinBox.Text);
            SaveRememberedPath("NRG2BIN", "OutputCuePath", Nrg2BinOutputCueBox.Text);
            _userSettings.Set("NRG2BIN", "SaveSubchannel", Checked(Nrg2BinSaveSubCheckBox));
            _userSettings.Set("NRG2BIN", "SendToFindCRCs", Checked(Nrg2BinSendToFindCrcsCheckBox));

            SaveRememberedPath("CDI2BIN", "InputPath", Cdi2BinInputBox.Text);
            SaveRememberedPath("CDI2BIN", "OutputBinPath", Cdi2BinOutputBinBox.Text);
            SaveRememberedPath("CDI2BIN", "OutputCuePath", Cdi2BinOutputCueBox.Text);
            _userSettings.Set("CDI2BIN", "SaveSubchannel", Checked(Cdi2BinSaveSubCheckBox));
            _userSettings.Set("CDI2BIN", "SendToFindCRCs", Checked(Cdi2BinSendToFindCrcsCheckBox));

            SaveRememberedPath("SkeleTool", "SkeletonPath", SkeletonPathBox.Text);
            SaveRememberedPath("SkeleTool", "HashPath", SkeletonHashBox.Text);
            SaveRememberedPath("SkeleTool", "SourceFolder", SkeletonSourceFolderBox.Text);
            SaveRememberedPath("SkeleTool", "SourceImage", SkeletonSourceImageBox.Text);
            SaveRememberedPath("SkeleTool", "OutputPath", SkeletonOutputBox.Text);
            _userSettings.Set("SkeleTool", "Recursive", Checked(SkeletonRecursiveCheckBox));
            _userSettings.Set("SkeleTool", "AllowMissing", Checked(SkeletonAllowMissingCheckBox));
            _userSettings.Set("SkeleTool", "ForceRehash", Checked(SkeletonForceRehashCheckBox));

            SaveRememberedPath("DIC", "LogPath", DicLogPathBox.Text);
            SaveRememberedPath("DIC", "SourceFolder", DicSourceFolderBox.Text);
            SaveRememberedPath("DIC", "DonorImagePath", DicDonorImageBox.Text);
            SaveRememberedPath("DIC", "OutputPath", DicOutputBox.Text);
            _userSettings.Set("DIC", "AllowMissing", Checked(DicAllowMissingCheckBox));
            _userSettings.Set("DIC", "ForceRehash", Checked(DicForceRehashCheckBox));
            _userSettings.Set("DIC", "verbose", Checked(DicVerboseLoggingCheckBox));
            _userSettings.RemoveKey("DIC", "VerboseLogging");

            SaveRememberedPath("IRD", "IrdPath", IrdPathBox.Text);
            SaveRememberedPath("IRD", "SourceFolder", IrdSourceFolderBox.Text);
            SaveRememberedPath("IRD", "OutputPath", IrdOutputBox.Text);
            SaveRememberedPath("IRD", "KeyFilePath", IrdKeyFileBox.Text);
            _userSettings.Set("IRD", "EncryptOutput", Checked(IrdEncryptCheckBox));
            // Never persist IrdKeyTextBox: it may contain the actual disc key.

            SaveRememberedPath("Concatenate", "DestinationPath", ConcatDestinationBox.Text);
            _userSettings.Set("Concatenate", "ZeroPadding", Checked(ConcatPaddingCheckBox));
            _userSettings.Set("Concatenate", "PaddingBytes", Text(ConcatPaddingBytesBox.Text));
            _userSettings.Set("Concatenate", "SkipUnsafeBoundaries", Checked(ConcatBoundaryCheckBox));

            SaveRememberedPath("HashCalc", "FilePath", HashCalcFileBox.Text);
            _userSettings.Set("HashCalc", "CRC32", Checked(HashCalcCrc32CheckBox));
            _userSettings.Set("HashCalc", "MD5", Checked(HashCalcMd5CheckBox));
            _userSettings.Set("HashCalc", "SHA1", Checked(HashCalcSha1CheckBox));
            _userSettings.Set("HashCalc", "SHA256", Checked(HashCalcSha256CheckBox));
            _userSettings.Set("HashCalc", "SHA384", Checked(HashCalcSha384CheckBox));
            _userSettings.Set("HashCalc", "SHA512", Checked(HashCalcSha512CheckBox));

            _userSettings.Set("Base64", "Operation", Base64OperationBox.SelectedIndex);
            _userSettings.Set("Base64", "InputType", Base64InputTypeBox.SelectedIndex);
            SaveRememberedPath("Base64", "InputFilePath", Base64InputFileBox.Text);
            SaveRememberedPath("Base64", "OutputFilePath", Base64OutputFileBox.Text);

            SaveRememberedPath("FindEnds", "PartialPath", FindEndsPartialBox.Text);
            SaveRememberedPath("FindEnds", "SourcePath", FindEndsSourceBox.Text);
            SaveRememberedPath("FindEnds", "OutputPath", FindEndsOutputBox.Text);
            _userSettings.Set("FindEnds", "Mode", FindEndsModeBox.SelectedIndex);

            SaveRememberedPath("ISOExtractor", "ImagePath", IsoExtractImagePathBox.Text);
            SaveRememberedPath("ISOExtractor", "OutputFolder", IsoExtractOutputFolderBox.Text);

            _userSettings.Save();
        }
        catch
        {
            // Persistence is a convenience feature. Never interrupt recovery or
            // conversion work because the settings file became unwritable.
        }
    }

    private void ClearSavedInputsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || string.IsNullOrWhiteSpace(control.Name))
            return;

        string? section = control.Name switch
        {
            "FindCrcsClearSavedInputsButton" => "FindCRCs",
            "AudioClearSavedInputsButton" => "Audio",
            "IsoClearSavedInputsButton" => "ISO2BIN",
            "Mdf2BinClearSavedInputsButton" => "MDF2BIN",
            "Nrg2BinClearSavedInputsButton" => "NRG2BIN",
            "Cdi2BinClearSavedInputsButton" => "CDI2BIN",
            "SkeletonClearSavedInputsButton" => "SkeleTool",
            "DicClearSavedInputsButton" => "DIC",
            "IrdClearSavedInputsButton" => "IRD",
            "ConcatClearSavedInputsButton" => "Concatenate",
            "HashCalcClearSavedInputsButton" => "HashCalc",
            "Base64ClearSavedInputsButton" => "Base64",
            "FindEndsClearSavedInputsButton" => "FindEnds",
            "IsoExtractClearSavedInputsButton" => "ISOExtractor",
            _ => null
        };

        if (section is null || IsSettingsSectionBusy(section))
            return;

        _userSettings?.RemoveSection(section);
        ResetSavedInputs(section);
        SaveUserSettings();
    }

    private async void SettingsResetMainIniButton_Click(object? sender, RoutedEventArgs e)
    {
        if (AnyOperationBusy())
        {
            await ShowMessageAsync(
                "Reset DumpToolbox.ini",
                "DumpToolbox.ini cannot be reset while an operation is running.");
            return;
        }

        bool confirmed = await ShowConfirmationAsync(
            "Reset DumpToolbox.ini",
            "Reset and recreate DumpToolbox.ini?\n\n" +
            "All custom configuration stored in this file will be lost, including saved paths, window size/position, selected tabs, custom theme colours and other preferences.\n\n" +
            "EOFSlackRules.ini is not affected.",
            "Reset and recreate");
        if (!confirmed)
            return;

        ResetMainSettingsIni();
    }

    private async void SettingsResetEofRulesButton_Click(object? sender, RoutedEventArgs e)
    {
        bool confirmed = await ShowConfirmationAsync(
            "Reset EOFSlackRules.ini",
            "Reset and recreate EOFSlackRules.ini?\n\n" +
            "Any custom EOF slack rules, edits, enabled/disabled states or other changes in the current file will be permanently lost and replaced with DumpToolbox's built-in seed rules.\n\n" +
            "DumpToolbox.ini and its saved paths/theme/window settings are not affected.",
            "Reset and recreate");
        if (!confirmed)
            return;

        string path = DumpToolbox.Core.EofSlackRuleService.ExternalFilePath;
        try
        {
            if (File.Exists(path))
                File.Delete(path);

            if (!DumpToolbox.Core.EofSlackRuleService.EnsureDefaultFileBesideExecutable(out string? error))
            {
                await ShowMessageAsync(
                    "Reset EOFSlackRules.ini",
                    error ?? $"Could not recreate '{path}'.");
                return;
            }

            await ShowMessageAsync(
                "Reset EOFSlackRules.ini",
                $"EOFSlackRules.ini has been recreated from the built-in seed data.\n\n{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync(
                "Reset EOFSlackRules.ini",
                $"Could not reset '{path}': {ex.Message}");
        }
    }

    private async void SettingsResetJolietNamingRulesButton_Click(object? sender, RoutedEventArgs e)
    {
        bool confirmed = await ShowConfirmationAsync(
            "Reset JolietNamingRules.ini",
            "Reset and recreate JolietNamingRules.ini?\n\nAny custom mastering-specific Joliet/ISO9660 naming rules or edits will be permanently lost and replaced with DumpToolbox's built-in seed profiles.",
            "Reset and recreate");
        if (!confirmed) return;

        string path = DumpToolbox.Core.JolietNamingRuleService.ExternalFilePath;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (!DumpToolbox.Core.JolietNamingRuleService.EnsureDefaultFileBesideExecutable(out string? error))
            {
                await ShowMessageAsync("Reset JolietNamingRules.ini", error ?? $"Could not recreate '{path}'.");
                return;
            }
            await ShowMessageAsync("Reset JolietNamingRules.ini", $"JolietNamingRules.ini has been recreated from the built-in seed data.\n\n{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Reset JolietNamingRules.ini", $"Could not reset '{path}': {ex.Message}");
        }
    }

    private bool AnyOperationBusy()
        => _findCrcsCts is not null || _audioRecoveryCts is not null || _iso2BinCts is not null ||
           _mdf2BinCts is not null || _nrg2BinCts is not null || _cdi2BinCts is not null || _skeletonCts is not null || _dicCts is not null || _irdCts is not null ||
           _concatenateCts is not null || _hashCalcCts is not null || _base64Cts is not null ||
           _findEndsCts is not null || _isoExtractorCts is not null ||
           _sha1CatalogueCts is not null || _discEvidenceCts is not null;

    private void ResetMainSettingsIni()
    {
        string? settingsPath = _userSettings?.FilePath;
        try
        {
            if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath))
                File.Delete(settingsPath);
            _userSettings = IniSettingsStore.Open();
        }
        catch
        {
            _userSettings = null;
        }

        _loadingUserSettings = true;
        try
        {
            ResetSavedInputs("FindCRCs");
            ResetSavedInputs("Audio");
            ResetSavedInputs("ISO2BIN");
            ResetSavedInputs("MDF2BIN");
            ResetSavedInputs("NRG2BIN");
            ResetSavedInputs("CDI2BIN");
            ResetSavedInputs("SkeleTool");
            ResetSavedInputs("DIC");
            ResetSavedInputs("IRD");
            ResetSavedInputs("Concatenate");
            ResetSavedInputs("HashCalc");
            ResetSavedInputs("Base64");
            ResetSavedInputs("FindEnds");
            ResetSavedInputs("ISOExtractor");
            MainTabControl.SelectedIndex = 0;
            ConvertTabControl.SelectedIndex = 0;
            OtherToolsTabControl.SelectedIndex = 0;
            SettingsThemeSystemRadio.IsChecked = true;
            SettingsThemeLightRadio.IsChecked = false;
            SettingsThemeDarkRadio.IsChecked = false;
            SettingsThemeCustomRadio.IsChecked = false;
            SettingsCustomBackgroundPicker.Color = Color.Parse("#202020");
            SettingsCustomTextPicker.Color = Color.Parse("#F2F2F2");
            SettingsCustomAccentPicker.Color = Color.Parse("#0078D4");
            SettingsCustomInputPicker.Color = Color.Parse("#2B2B2B");
            SettingsCustomThemePanel.IsVisible = false;
            SettingsRememberPathsCheckBox.IsChecked = true;
            SettingsMenuHorizontalRadio.IsChecked = true;
            SettingsMenuVerticalRadio.IsChecked = false;
            ApplyMainMenuLayout(vertical: false);
            SettingsSha1DatabaseCheckBox.IsChecked = true;
            ApplySelectedTheme();

            WindowState = WindowState.Normal;
            Width = 1060;
            Height = 780;
            _lastNormalClientSize = new Size(1060, 780);
            _normalSizeInitialized = true;
        }
        finally
        {
            _loadingUserSettings = false;
        }

        // Do not immediately write the old screen coordinates back into the
        // freshly-reset INI. If the user moves the window afterwards, the
        // PositionChanged handler resumes normal position persistence.
        _suppressWindowPositionPersistence = true;
        SaveUserSettings();
    }

    private bool IsSettingsSectionBusy(string section)
        => section switch
        {
            "FindCRCs" => _findCrcsCts is not null,
            "Audio" => _audioRecoveryCts is not null,
            "ISO2BIN" => _iso2BinCts is not null,
            "MDF2BIN" => _mdf2BinCts is not null,
            "NRG2BIN" => _nrg2BinCts is not null,
            "CDI2BIN" => _cdi2BinCts is not null,
            "SkeleTool" => _skeletonCts is not null,
            "DIC" => _dicCts is not null,
            "IRD" => _irdCts is not null,
            "Concatenate" => _concatenateCts is not null,
            "HashCalc" => _hashCalcCts is not null,
            "Base64" => _base64Cts is not null,
            "FindEnds" => _findEndsCts is not null,
            "ISOExtractor" => _isoExtractorCts is not null,
            _ => false
        };

    private void ResetSavedInputs(string section)
    {
        switch (section)
        {
            case "FindCRCs":
                FilePathBox.Text = string.Empty;
                FindCrcsCueBox.Text = string.Empty;
                _findCrcsCueAnalysis = null;
                FindCrcsEdgeRepairCheckBox.IsChecked = false;
                FindCrcsPregapScrambleCheckBox.IsChecked = false;
                FindCrcsSavePartialCheckBox.IsChecked = false;
                UpdateFindCrcsCueControls();
                break;
            case "Audio":
                AudioOutputFolderBox.Text = string.Empty;
                AudioEdgeSecondsBox.Text = "5";
                AudioEdgeRepairCheckBox.IsChecked = false;
                AudioSavePartialCheckBox.IsChecked = false;
                AudioHeadsTailsCheckBox.IsChecked = false;
                AudioDeleteTempCheckBox.IsChecked = false;
                break;
            case "ISO2BIN":
                IsoInputBox.Text = string.Empty;
                IsoCueBox.Text = string.Empty;
                IsoXaMetadataBox.Text = string.Empty;
                IsoOutputBox.Text = string.Empty;
                IsoModeBox.SelectedIndex = 0;
                IsoSendToFindCrcsCheckBox.IsChecked = false;
                break;
            case "MDF2BIN":
                Mdf2BinMdsBox.Text = string.Empty;
                Mdf2BinMdfBox.Text = string.Empty;
                Mdf2BinOutputBinBox.Text = string.Empty;
                Mdf2BinOutputCueBox.Text = string.Empty;
                Mdf2BinSaveSubCheckBox.IsChecked = false;
                Mdf2BinSendToFindCrcsCheckBox.IsChecked = false;
                break;
            case "NRG2BIN":
                Nrg2BinInputBox.Text = string.Empty;
                Nrg2BinOutputBinBox.Text = string.Empty;
                Nrg2BinOutputCueBox.Text = string.Empty;
                Nrg2BinSaveSubCheckBox.IsChecked = false;
                Nrg2BinSendToFindCrcsCheckBox.IsChecked = false;
                _nrg2BinInspection = null;
                Nrg2BinInspectionText.Text = "No NRG analysed.";
                break;
            case "CDI2BIN":
                Cdi2BinInputBox.Text = string.Empty;
                Cdi2BinOutputBinBox.Text = string.Empty;
                Cdi2BinOutputCueBox.Text = string.Empty;
                Cdi2BinSaveSubCheckBox.IsChecked = false;
                Cdi2BinSendToFindCrcsCheckBox.IsChecked = false;
                _cdi2BinInspection = null;
                Cdi2BinInspectionText.Text = "No CDI analysed.";
                break;
            case "SkeleTool":
                SkeletonPathBox.Text = string.Empty;
                SkeletonHashBox.Text = string.Empty;
                SkeletonSourceFolderBox.Text = string.Empty;
                SkeletonSourceImageBox.Text = string.Empty;
                SkeletonOutputBox.Text = string.Empty;
                SkeletonRecursiveCheckBox.IsChecked = true;
                SkeletonAllowMissingCheckBox.IsChecked = false;
                SkeletonForceRehashCheckBox.IsChecked = false;
                break;
            case "DIC":
                DicLogPathBox.Text = string.Empty;
                DicSourceFolderBox.Text = string.Empty;
                DicDonorImageBox.Text = string.Empty;
                DicOutputBox.Text = string.Empty;
                DicAllowMissingCheckBox.IsChecked = false;
                DicForceRehashCheckBox.IsChecked = false;
                DicVerboseLoggingCheckBox.IsChecked = false;
                break;
            case "IRD":
                IrdPathBox.Text = string.Empty;
                IrdSourceFolderBox.Text = string.Empty;
                IrdOutputBox.Text = string.Empty;
                IrdKeyFileBox.Text = string.Empty;
                IrdKeyTextBox.Text = string.Empty;
                IrdEncryptCheckBox.IsChecked = false;
                _irdVerification = null;
                _irdVerifiedIrdPath = null;
                _irdVerifiedSourceFolder = null;
                _irdTreeRoots.Clear();
                _irdNodes.Clear();
                IrdInspectionText.Text = "No IRD loaded.";
                IrdProgressBar.Value = 0;
                IrdProgressText.Text = "Ready";
                IrdRebuildButton.IsEnabled = false;
                break;
            case "Concatenate":
                ConcatDestinationBox.Text = string.Empty;
                ConcatPaddingCheckBox.IsChecked = false;
                ConcatPaddingBytesBox.Text = "10240";
                ConcatBoundaryCheckBox.IsChecked = true;
                break;
            case "HashCalc":
                HashCalcFileBox.Text = string.Empty;
                HashCalcCrc32CheckBox.IsChecked = true;
                HashCalcMd5CheckBox.IsChecked = true;
                HashCalcSha1CheckBox.IsChecked = true;
                HashCalcSha256CheckBox.IsChecked = false;
                HashCalcSha384CheckBox.IsChecked = false;
                HashCalcSha512CheckBox.IsChecked = false;
                break;
            case "Base64":
                Base64OperationBox.SelectedIndex = 0;
                Base64InputTypeBox.SelectedIndex = 0;
                Base64InputFileBox.Text = string.Empty;
                Base64OutputFileBox.Text = string.Empty;
                break;
            case "FindEnds":
                FindEndsPartialBox.Text = string.Empty;
                FindEndsSourceBox.Text = string.Empty;
                FindEndsOutputBox.Text = string.Empty;
                FindEndsModeBox.SelectedIndex = 0;
                break;
            case "ISOExtractor":
                IsoExtractImagePathBox.Text = string.Empty;
                IsoExtractOutputFolderBox.Text = string.Empty;
                break;
        }
    }

    private bool RememberLastUsedPaths => SettingsRememberPathsCheckBox.IsChecked == true;
    private bool IsSha1DatabaseEnabled => SettingsSha1DatabaseCheckBox.IsChecked == true;

    private void LoadRememberedPath(TextBox textBox, string section, string key)
    {
        textBox.Text = RememberLastUsedPaths && _userSettings is not null
            ? _userSettings.Get(section, key)
            : string.Empty;
    }

    private void SaveRememberedPath(string section, string key, string? value)
    {
        if (_userSettings is null)
            return;
        if (RememberLastUsedPaths)
            _userSettings.Set(section, key, Text(value));
        else
            _userSettings.RemoveKey(section, key);
    }

    private void RemoveAllRememberedPaths()
    {
        if (_userSettings is null)
            return;

        (string Section, string Key)[] keys =
        {
            ("FindCRCs", "SourcePath"), ("FindCRCs", "CuePath"),
            ("Audio", "OutputFolder"),
            ("ISO2BIN", "InputPath"), ("ISO2BIN", "CuePath"), ("ISO2BIN", "XaMetadataPath"), ("ISO2BIN", "OutputPath"),
            ("MDF2BIN", "MdsPath"), ("MDF2BIN", "MdfPath"), ("MDF2BIN", "OutputBinPath"), ("MDF2BIN", "OutputCuePath"),
            ("SkeleTool", "SkeletonPath"), ("SkeleTool", "HashPath"), ("SkeleTool", "SourceFolder"), ("SkeleTool", "SourceImage"), ("SkeleTool", "OutputPath"),
            ("DIC", "LogPath"), ("DIC", "SourceFolder"), ("DIC", "DonorImagePath"), ("DIC", "OutputPath"),
            ("Concatenate", "DestinationPath"),
            ("HashCalc", "FilePath"),
            ("Base64", "InputFilePath"), ("Base64", "OutputFilePath"),
            ("FindEnds", "PartialPath"), ("FindEnds", "SourcePath"), ("FindEnds", "OutputPath"),
            ("ISOExtractor", "ImagePath"), ("ISOExtractor", "OutputFolder")
        };

        foreach ((string section, string key) in keys)
            _userSettings.RemoveKey(section, key);
    }

    private string GetSelectedThemeName()
        => SettingsThemeCustomRadio.IsChecked == true ? "Custom"
         : SettingsThemeLightRadio.IsChecked == true ? "Light"
         : SettingsThemeDarkRadio.IsChecked == true ? "Dark"
         : "System";

    private void ApplySelectedTheme()
        => ApplyTheme(GetSelectedThemeName());

    private void ApplyTheme(string themeName)
    {
        if (Application.Current is null)
            return;

        bool custom = themeName.Equals("Custom", StringComparison.OrdinalIgnoreCase);
        SettingsCustomThemePanel.IsVisible = custom;

        if (custom)
        {
            Color background = SettingsCustomBackgroundPicker.Color;
            Color text = SettingsCustomTextPicker.Color;
            Color accent = SettingsCustomAccentPicker.Color;
            Color input = SettingsCustomInputPicker.Color;

            Application.Current.RequestedThemeVariant = IsDarkThemeColor(background)
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
            Application.Current.Resources["CustomThemeBackgroundBrush"] = new SolidColorBrush(background);
            Application.Current.Resources["CustomThemeTextBrush"] = new SolidColorBrush(text);
            Application.Current.Resources["CustomThemeAccentBrush"] = new SolidColorBrush(accent);
            Application.Current.Resources["CustomThemeInputBrush"] = new SolidColorBrush(input);
            Application.Current.Resources["SystemAccentColor"] = accent;

            if (!Classes.Contains("custom-theme"))
                Classes.Add("custom-theme");
        }
        else
        {
            Classes.Remove("custom-theme");
            Application.Current.Resources.Remove("SystemAccentColor");
            Application.Current.RequestedThemeVariant = themeName switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
    }

    private void ApplyThemeClassToWindow(Window window)
    {
        if (GetSelectedThemeName().Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            if (!window.Classes.Contains("custom-theme"))
                window.Classes.Add("custom-theme");
        }
        else
        {
            window.Classes.Remove("custom-theme");
        }
    }

    private void SettingsThemeRadio_Checked(object? sender, RoutedEventArgs e)
    {
        if (_loadingUserSettings)
            return;

        string selectedTheme = ReferenceEquals(sender, SettingsThemeCustomRadio) ? "Custom"
            : ReferenceEquals(sender, SettingsThemeLightRadio) ? "Light"
            : ReferenceEquals(sender, SettingsThemeDarkRadio) ? "Dark"
            : "System";

        ApplyTheme(selectedTheme);
        Dispatcher.UIThread.Post(SaveUserSettings);
    }

    private void SettingsCustomColorPicker_ColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (_loadingUserSettings || SettingsThemeCustomRadio.IsChecked != true)
            return;

        ApplyTheme("Custom");
        Dispatcher.UIThread.Post(SaveUserSettings);
    }

    private static Color ParseThemeColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        try
        {
            Color parsed = Color.Parse(value);
            return Color.FromArgb(255, parsed.R, parsed.G, parsed.B);
        }
        catch
        {
            return fallback;
        }
    }

    private static string ThemeColorToString(Color color)
        => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool IsDarkThemeColor(Color color)
        => ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) < 128.0;

    private void SettingsMenuLayoutRadio_Checked(object? sender, RoutedEventArgs e)
    {
        if (_loadingUserSettings || _updatingMenuLayoutSelection)
            return;

        bool vertical = ReferenceEquals(sender, SettingsMenuVerticalRadio);

        // Keep the pair explicitly mutually exclusive.  Avalonia RadioButton
        // grouping can be disrupted when the TabControl changes its tab-strip
        // placement at runtime, leaving both buttons checked.
        try
        {
            _updatingMenuLayoutSelection = true;
            SettingsMenuHorizontalRadio.IsChecked = !vertical;
            SettingsMenuVerticalRadio.IsChecked = vertical;
        }
        finally
        {
            _updatingMenuLayoutSelection = false;
        }

        ApplyMainMenuLayout(vertical);
        Dispatcher.UIThread.Post(SaveUserSettings);
    }

    private void ApplyMainMenuLayout(bool vertical)
    {
        MainTabControl.TabStripPlacement = vertical ? Dock.Left : Dock.Top;
    }

    private void SettingsPreferenceChanged_Click(object? sender, RoutedEventArgs e)
    {
        if (!_loadingUserSettings)
        {
            SaveUserSettings();
            UpdateSkeletonActionButtons();
        }
    }

    private void SettingsRememberPathsCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (_loadingUserSettings)
            return;
        if (!RememberLastUsedPaths)
            RemoveAllRememberedPaths();
        SaveUserSettings();
    }

    private async void SettingsAboutButton_Click(object? sender, RoutedEventArgs e)
        => await ShowMessageAsync("About DumpToolbox", $"DumpToolbox\nVersion {GetApplicationVersion()}");

    private static string GetApplicationVersion()
    {
        Version? version = typeof(MainWindow).Assembly.GetName().Version;
        return version is { Build: >= 0 }
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : version?.ToString() ?? "unknown";
    }

    private static string Text(string? value) => value?.Trim() ?? string.Empty;
    private static bool Checked(CheckBox checkBox) => checkBox.IsChecked == true;

    private static void SetSelectedIndex(SelectingItemsControl control, int index)
    {
        if (index >= 0)
            control.SelectedIndex = index;
    }
}
