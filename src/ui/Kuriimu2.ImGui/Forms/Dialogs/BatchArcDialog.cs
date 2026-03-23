using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImGui.Forms.Controls;
using ImGui.Forms.Controls.Layouts;
using ImGui.Forms.Controls.Text;
using ImGui.Forms.Controls.Text.Editor;
using ImGui.Forms.Modals;
using ImGui.Forms.Modals.IO;
using ImGui.Forms.Models;
using Konnect.Contract.DataClasses.FileSystem;
using Konnect.Contract.DataClasses.Management.Files;
using Konnect.Contract.Enums.Management.Files;
using Konnect.Contract.FileSystem;
using Konnect.Contract.Management.Files;
using Konnect.Contract.Management.Plugin;
using Konnect.Contract.Plugin.File;
using Konnect.Contract.Plugin.File.Archive;
using Konnect.Extensions;
using Konnect.FileSystem;
using Konnect.Management.Streams;
using Kuriimu2.ImGui.Resources;
using Serilog;

namespace Kuriimu2.ImGui.Forms.Dialogs
{
    enum BatchArcOperation
    {
        Extract,
        Reimport
    }

    partial class BatchArcDialog : Modal
    {
        private readonly BatchArcOperation _operation;
        private readonly IFileManager _fileManager;
        private readonly ILogger _logger;
        private readonly IFilePlugin[] _arcPlugins;

        private StackLayout _mainLayout;
        private StackLayout _settingsLayout;
        private ComboBox<IFilePlugin> _pluginComboBox;
        private TextBox _inputTextBox;
        private TextBox _outputTextBox;
        private Button _inputFolderBtn;
        private Button _outputFolderBtn;
        private CheckBox _subDirCheckBox;
        private Button _executeBtn;
        private TextEditor _logEditor;
        private ProgressBar _progress;

        public BatchArcDialog(BatchArcOperation operation, IPluginManager pluginManager, IFileManager fileManager, ILogger logger)
        {
            _operation = operation;
            _fileManager = fileManager;
            _logger = logger;
            _arcPlugins = pluginManager.GetPlugins<IFilePlugin>()
                .Where(x => x.FileExtensions?.Any(ext => string.Equals(ext, "*.arc", StringComparison.OrdinalIgnoreCase)) ?? false)
                .OrderBy(x => x.Metadata?.Name ?? x.GetType().Name)
                .ToArray();

            InitializeComponent();

            _inputFolderBtn.Clicked += _inputFolderBtn_Clicked;
            _outputFolderBtn.Clicked += _outputFolderBtn_Clicked;
            _executeBtn.Clicked += _executeBtn_Clicked;
            _pluginComboBox.SelectedItemChanged += (_, _) => UpdateFormInternal();

            UpdateFormInternal();
        }

        private void InitializeComponent()
        {
            _pluginComboBox = new ComboBox<IFilePlugin> { Alignment = ComboBoxAlignment.Bottom, MaxShowItems = 12 };
            foreach (var plugin in _arcPlugins)
            {
                var pluginName = plugin.Metadata?.Name ?? plugin.GetType().Name;
                _pluginComboBox.Items.Add(new DropDownItem<IFilePlugin>(plugin, pluginName));
            }
            _pluginComboBox.SelectedItem = _pluginComboBox.Items.FirstOrDefault();

            _inputTextBox = new TextBox { IsReadOnly = true, Placeholder = LocalizationResources.BatchArcInputPlaceholder };
            _outputTextBox = new TextBox { IsReadOnly = true, Placeholder = LocalizationResources.BatchArcOutputPlaceholder };
            _inputFolderBtn = new Button { Width = SizeValue.Parent, Text = LocalizationResources.BatchArcInputFolder };
            _outputFolderBtn = new Button { Width = SizeValue.Parent, Text = LocalizationResources.BatchArcOutputFolder };
            _subDirCheckBox = new CheckBox { Text = LocalizationResources.BatchArcSearchSubfolders };
            _executeBtn = new Button { Width = SizeValue.Parent, Text = _operation == BatchArcOperation.Extract ? LocalizationResources.BatchArcExecuteExtract : LocalizationResources.BatchArcExecuteReimport, KeyAction = new(Key.Enter) };
            _logEditor = new TextEditor { IsReadOnly = true };
            _progress = new ProgressBar { Size = new Size(SizeValue.Parent, 24), ProgressColor = ColorResources.Progress, Text = LocalizationResources.BatchArcProgress };

            _settingsLayout = new StackLayout
            {
                Alignment = Alignment.Vertical,
                Size = new Size(.5f, SizeValue.Parent),
                ItemSpacing = 4,
                Items =
                {
                    _pluginComboBox,
                    _inputTextBox,
                    _inputFolderBtn,
                    _outputTextBox,
                    _outputFolderBtn,
                    _subDirCheckBox,
                    new StackItem(_executeBtn){Size = Size.Parent, VerticalAlignment = VerticalAlignment.Bottom},
                    new StackItem(_progress){Size = Size.WidthAlign}
                }
            };

            _mainLayout = new StackLayout
            {
                Alignment = Alignment.Horizontal,
                Size = Size.Parent,
                ItemSpacing = 4,
                Items =
                {
                    _settingsLayout,
                    new Splitter(Alignment.Vertical),
                    new StackItem(_logEditor) { Size = new Size(.5f, SizeValue.Parent) }
                }
            };

            Caption = _operation == BatchArcOperation.Extract ? LocalizationResources.BatchArcCaptionExtract : LocalizationResources.BatchArcCaptionReimport;
            Size = new Size(SizeValue.Relative(.6f), SizeValue.Relative(.6f));
            Content = _mainLayout;
        }

        private async void _executeBtn_Clicked(object? sender, EventArgs e)
        {
            _executeBtn.Enabled = false;
            await Task.Run(Process);
            _executeBtn.Enabled = true;
        }

        private async void _inputFolderBtn_Clicked(object? sender, EventArgs e)
        {
            var folderPath = await SelectFolder();
            if (folderPath is null)
                return;

            _inputTextBox.Text = folderPath;
            UpdateFormInternal();
        }

        private async void _outputFolderBtn_Clicked(object? sender, EventArgs e)
        {
            var folderPath = await SelectFolder();
            if (folderPath is null)
                return;

            _outputTextBox.Text = folderPath;
            UpdateFormInternal();
        }

        private void UpdateFormInternal()
        {
            _executeBtn.Enabled = _pluginComboBox.SelectedItem is not null && !string.IsNullOrWhiteSpace(_inputTextBox.Text) && !string.IsNullOrWhiteSpace(_outputTextBox.Text);
        }

        private async Task<string?> SelectFolder()
        {
            var sfd = new SelectFolderDialog { Directory = SettingsResources.LastDirectory };
            var result = await sfd.ShowAsync();
            if (result != DialogResult.Ok)
                return null;

            SettingsResources.LastDirectory = sfd.Directory;
            return sfd.Directory;
        }

        private void Process()
        {
            _logEditor.SetText(string.Empty);
            _progress.Value = 0;

            var searchOptions = _subDirCheckBox.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var arcFiles = Directory.EnumerateFiles(_inputTextBox.Text, "*.arc", searchOptions).OrderBy(x => x).ToArray();
            _progress.Maximum = Math.Max(arcFiles.Length, 1);

            foreach (var arcFile in arcFiles)
                ProcessFile(arcFile);

            if (arcFiles.Length == 0)
                AppendLog(LocalizationResources.BatchArcLogNoFiles);
        }

        private void ProcessFile(string arcFile)
        {
            var relativePath = Path.GetRelativePath(_inputTextBox.Text, arcFile);
            AppendLog(LocalizationResources.BatchArcLogProcess(relativePath));

            var plugin = _pluginComboBox.SelectedItem?.Content;
            if (plugin is null)
                return;

            try
            {
                var sourceFs = FileSystemFactory.CreateSubFileSystem(_inputTextBox.Text, new StreamManager());
                var targetFs = FileSystemFactory.CreateSubFileSystem(_outputTextBox.Text, new StreamManager());
                var filePath = sourceFs.ConvertPathFromInternal(arcFile).ToRelative();

                if (_operation == BatchArcOperation.Extract)
                    ExtractArchive(sourceFs, targetFs, filePath, plugin).GetAwaiter().GetResult();
                else
                    ReimportArchive(sourceFs, targetFs, filePath, plugin).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Batch ARC processing failed for {File}.", arcFile);
                AppendLog(LocalizationResources.BatchArcLogError(relativePath));
            }
            finally
            {
                _progress.Value++;
            }
        }

        private async Task ExtractArchive(IFileSystem sourceFileSystem, IFileSystem destinationFileSystem, UPath filePath, IFilePlugin plugin)
        {
            var loadedFile = await _fileManager.LoadFile(sourceFileSystem, filePath, plugin.PluginId);
            if (loadedFile.Status != LoadStatus.Successful || loadedFile.LoadedFileState?.PluginState is not IArchiveFilePluginState archiveState)
                return;

            try
            {
                var outputRoot = filePath.GetDirectory() / filePath.GetNameWithoutExtension();
                if (archiveState.Files.Count > 0)
                    destinationFileSystem.CreateDirectory(outputRoot);

                foreach (var afi in archiveState.Files)
                {
                    var destinationPath = outputRoot / afi.FilePath.ToRelative();
                    destinationFileSystem.CreateDirectory(destinationPath.GetDirectory());

                    await using var output = await destinationFileSystem.OpenFileAsync(destinationPath, FileMode.Create, FileAccess.Write);
                    await using var input = await afi.GetFileData();
                    await input.CopyToAsync(output);
                }
            }
            finally
            {
                _fileManager.Close(loadedFile.LoadedFileState!);
            }
        }

        private async Task ReimportArchive(IFileSystem sourceFileSystem, IFileSystem replacementFileSystem, UPath filePath, IFilePlugin plugin)
        {
            var loadedFile = await _fileManager.LoadFile(sourceFileSystem, filePath, plugin.PluginId);
            if (loadedFile.Status != LoadStatus.Successful || loadedFile.LoadedFileState?.PluginState is not IArchiveFilePluginState archiveState)
                return;

            try
            {
                var replacementRoot = filePath.GetDirectory() / filePath.GetNameWithoutExtension();
                if (!replacementFileSystem.DirectoryExists(replacementRoot))
                    return;

                foreach (var afi in archiveState.Files)
                {
                    var replacementPath = replacementRoot / afi.FilePath.ToRelative();
                    if (!replacementFileSystem.FileExists(replacementPath))
                        continue;

                    using var input = replacementFileSystem.OpenFile(replacementPath);
                    afi.SetFileData(input);
                }

                await _fileManager.SaveFile(loadedFile.LoadedFileState);
            }
            finally
            {
                _fileManager.Close(loadedFile.LoadedFileState!);
            }
        }

        private void AppendLog(string text)
        {
            var currentText = _logEditor.GetText();
            _logEditor.SetText(currentText + text + Environment.NewLine);
        }
    }
}
