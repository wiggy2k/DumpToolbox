using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private void InitializeUtilityTabs()
    {
        Base64OperationBox.SelectionChanged += Base64Mode_SelectionChanged;
        Base64InputTypeBox.SelectionChanged += Base64Mode_SelectionChanged;
        UpdateBase64ModeUi();
    }
}
