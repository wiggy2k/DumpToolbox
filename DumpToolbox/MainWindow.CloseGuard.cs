namespace DumpToolbox;

public partial class MainWindow
{
    private bool _closeConfirmed;
    private bool _closeConfirmationOpen;

    private void InitializeCloseGuard()
    {
        Closing += async (_, e) =>
        {
            if (_closeConfirmed)
                return;

            string[] active = GetActiveOperationNames();
            if (active.Length == 0)
                return;

            e.Cancel = true;
            if (_closeConfirmationOpen)
                return;

            _closeConfirmationOpen = true;
            try
            {
                string taskList = string.Join("\n", active.Select(name => $"• {name}"));
                string message = active.Length == 1
                    ? $"A task is still running:\n\n{taskList}\n\nClosing DumpToolbox now will stop the running task. Are you sure you want to exit?"
                    : $"{active.Length} tasks are still running:\n\n{taskList}\n\nClosing DumpToolbox now will stop these running tasks. Are you sure you want to exit?";

                bool confirmed = await ShowConfirmationAsync(
                    "Tasks still running",
                    message,
                    "Exit anyway");

                if (!confirmed)
                    return;

                _closeConfirmed = true;
                await Task.Yield();
                Close();
            }
            finally
            {
                _closeConfirmationOpen = false;
            }
        };
    }

    private string[] GetActiveOperationNames()
    {
        var active = new List<string>();

        if (_findCrcsCts is not null) active.Add("FindCRCs");
        if (_audioRecoveryCts is not null) active.Add("Audio Recovery");
        if (_iso2BinCts is not null) active.Add("ISO2BIN");
        if (_mdf2BinCts is not null) active.Add("MDF2BIN");
        if (_nrg2BinCts is not null) active.Add("NRG2BIN");
        if (_cdi2BinCts is not null) active.Add("CDI2BIN");
        if (_skeletonCts is not null) active.Add("SkeleTool");
        if (_dicCts is not null) active.Add("DIC");
        if (_irdCts is not null) active.Add("IRD");
        if (_concatenateCts is not null) active.Add("Concatenate");
        if (_hashCalcCts is not null) active.Add("Hash calculator");
        if (_base64Cts is not null) active.Add("Base64");
        if (_findEndsCts is not null) active.Add("Find Ends");
        if (_isoExtractorCts is not null) active.Add("ISO Extractor");
        if (_sha1CatalogueCts is not null) active.Add("SHA-1 Database scan");
        if (_audioHeadsTailsCts is not null) active.Add("Heads and Tails scan");
        if (_discEvidenceCts is not null) active.Add("Disc Evidence scan");

        return active.ToArray();
    }
}
