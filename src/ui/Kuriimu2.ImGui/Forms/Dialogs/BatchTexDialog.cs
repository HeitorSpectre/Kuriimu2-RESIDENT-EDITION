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
using Konnect.Contract.Plugin.File.Image;
using Konnect.Extensions;
using Konnect.FileSystem;
using Konnect.Management.Streams;
using Kuriimu2.ImGui.Resources;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;
using Size = ImGui.Forms.Models.Size;

namespace Kuriimu2.ImGui.Forms.Dialogs
{
    enum BatchTexOperation
    {
        Extract,
        Reimport
    }

    partial class BatchTexDialog : Modal
    {
        private static readonly Guid MtTexPluginId_ = Guid.Parse("9e85ef16-7157-40ba-846a-b5a17148775f");

        private readonly BatchTexOperation _operation;
        private readonly IFileManager _fileManager;
        private readonly ILogger _logger;

        private StackLayout _mainLayout;
        private StackLayout _settingsLayout;
        private TextBox _inputTextBox;
        private TextBox _outputTextBox;
        private Button _inputFolderBtn;
        private Button _outputFolderBtn;
        private CheckBox _subDirCheckBox;
        private CheckBox _deleteImportedPngCheckBox;
        private Button _executeBtn;
        private TextEditor _logEditor;
        private ProgressBar _progress;

        public BatchTexDialog(BatchTexOperation operation, IFileManager fileManager, ILogger logger)
        {
            _operation = operation;
            _fileManager = fileManager;
            _logger = logger;

            InitializeComponent();

            _inputFolderBtn.Clicked += _inputFolderBtn_Clicked;
            _outputFolderBtn.Clicked += _outputFolderBtn_Clicked;
            _executeBtn.Clicked += _executeBtn_Clicked;

            UpdateFormInternal();
        }

        private void InitializeComponent()
        {
            _inputTextBox = new TextBox { IsReadOnly = true, Placeholder = LocalizationResources.BatchTexInputPlaceholder };
            _outputTextBox = new TextBox { IsReadOnly = true, Placeholder = LocalizationResources.BatchTexOutputPlaceholder };
            _inputFolderBtn = new Button { Width = SizeValue.Parent, Text = LocalizationResources.BatchTexInputFolder };
            _outputFolderBtn = new Button { Width = SizeValue.Parent, Text = LocalizationResources.BatchTexOutputFolder };
            _subDirCheckBox = new CheckBox { Text = LocalizationResources.BatchTexSearchSubfolders };
            _deleteImportedPngCheckBox = new CheckBox
            {
                Text = LocalizationResources.BatchTexDeleteImportedPngs,
                Visible = _operation == BatchTexOperation.Reimport
            };
            _executeBtn = new Button { Width = SizeValue.Parent, Text = _operation == BatchTexOperation.Extract ? LocalizationResources.BatchTexExecuteExtract : LocalizationResources.BatchTexExecuteReimport };
            _logEditor = new TextEditor { IsReadOnly = true };
            _progress = new ProgressBar { Size = new Size(SizeValue.Parent, 24), ProgressColor = ColorResources.Progress, Text = LocalizationResources.BatchTexProgress };

            _settingsLayout = new StackLayout
            {
                Alignment = Alignment.Vertical,
                Size = new Size(.5f, SizeValue.Parent),
                ItemSpacing = 4,
                Items =
                {
                    _inputTextBox,
                    _inputFolderBtn,
                    _outputTextBox,
                    _outputFolderBtn,
                    _subDirCheckBox,
                    _deleteImportedPngCheckBox,
                    new StackItem(_executeBtn) { Size = Size.Parent, VerticalAlignment = VerticalAlignment.Bottom },
                    new StackItem(_progress) { Size = Size.WidthAlign }
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

            Caption = _operation == BatchTexOperation.Extract ? LocalizationResources.BatchTexCaptionExtract : LocalizationResources.BatchTexCaptionReimport;
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
            _executeBtn.Enabled = !string.IsNullOrWhiteSpace(_inputTextBox.Text) &&
                                  Directory.Exists(_inputTextBox.Text);
        }

        private string GetTargetRootPath()
        {
            if (!string.IsNullOrWhiteSpace(_outputTextBox.Text) && Directory.Exists(_outputTextBox.Text))
                return _outputTextBox.Text;

            return _inputTextBox.Text;
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
            var texFiles = Directory.EnumerateFiles(_inputTextBox.Text, "*.tex", searchOptions).OrderBy(x => x).ToArray();
            var targetRootPath = GetTargetRootPath();
            _progress.Maximum = Math.Max(texFiles.Length, 1);

            foreach (var texFile in texFiles)
                ProcessFile(texFile, targetRootPath);

            if (texFiles.Length == 0)
                AppendLog(LocalizationResources.BatchTexLogNoFiles);
        }

        private void ProcessFile(string texFile, string targetRootPath)
        {
            var relativePath = Path.GetRelativePath(_inputTextBox.Text, texFile);
            AppendLog(LocalizationResources.BatchTexLogProcess(relativePath));

            try
            {
                var sourceFs = FileSystemFactory.CreateSubFileSystem(_inputTextBox.Text, new StreamManager());
                var targetFs = FileSystemFactory.CreateSubFileSystem(targetRootPath, new StreamManager());
                var filePath = sourceFs.ConvertPathFromInternal(texFile).ToRelative();

                if (_operation == BatchTexOperation.Extract)
                    ExtractTex(sourceFs, targetFs, filePath).GetAwaiter().GetResult();
                else
                    ReimportTex(sourceFs, targetFs, filePath).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Batch TEX processing failed for {File}.", texFile);
                AppendLog(LocalizationResources.BatchTexLogError(relativePath));
            }
            finally
            {
                _progress.Value++;
            }
        }

        private async Task ExtractTex(IFileSystem sourceFileSystem, IFileSystem destinationFileSystem, UPath filePath)
        {
            var loadedFile = await _fileManager.LoadFile(sourceFileSystem, filePath, MtTexPluginId_);
            if (loadedFile.Status != LoadStatus.Successful)
            {
                AppendLog($"Falha ao abrir {filePath.FullName}: {loadedFile.Reason}");
                return;
            }

            if (loadedFile.LoadedFileState?.PluginState is not IImageFilePluginState imageState)
            {
                AppendLog($"O arquivo {filePath.FullName} nao foi carregado como imagem.");
                return;
            }

            try
            {
                var outputDirectory = filePath.GetDirectory();
                destinationFileSystem.CreateDirectory(outputDirectory);

                if (imageState.Images.Count <= 0)
                {
                    AppendLog($"Nenhuma imagem encontrada em {filePath.FullName}.");
                    return;
                }

                for (var i = 0; i < imageState.Images.Count; i++)
                {
                    var image = imageState.Images[i];
                    var destinationPath = outputDirectory / GetImageFileName(filePath, imageState.Images.Count, image, i);

                    await using var output = await destinationFileSystem.OpenFileAsync(destinationPath, FileMode.Create, FileAccess.Write);
                    await image.GetImage().SaveAsPngAsync(output);
                    AppendLog($"Exportado: {destinationPath.FullName}");
                }
            }
            finally
            {
                _fileManager.Close(loadedFile.LoadedFileState!);
            }
        }

        private async Task ReimportTex(IFileSystem sourceFileSystem, IFileSystem replacementFileSystem, UPath filePath)
        {
            var loadedFile = await _fileManager.LoadFile(sourceFileSystem, filePath, MtTexPluginId_);
            if (loadedFile.Status != LoadStatus.Successful)
            {
                AppendLog($"Falha ao abrir {filePath.FullName}: {loadedFile.Reason}");
                return;
            }

            if (loadedFile.LoadedFileState?.PluginState is not IImageFilePluginState imageState)
            {
                AppendLog($"O arquivo {filePath.FullName} nao foi carregado como imagem.");
                return;
            }

            try
            {
                var replacementDirectory = filePath.GetDirectory();
                if (!replacementFileSystem.DirectoryExists(replacementDirectory))
                {
                    AppendLog($"Pasta de reimportacao nao encontrada: {replacementDirectory.FullName}");
                    return;
                }

                var importedAnyImage = false;
                var importedPaths = new System.Collections.Generic.List<UPath>();
                for (var i = 0; i < imageState.Images.Count; i++)
                {
                    var image = imageState.Images[i];
                    var replacementPath = replacementDirectory / GetImageFileName(filePath, imageState.Images.Count, image, i);
                    if (!replacementFileSystem.FileExists(replacementPath))
                        continue;

                    await using var input = await replacementFileSystem.OpenFileAsync(replacementPath, FileMode.Open, FileAccess.Read);
                    using var newImage = Image.Load<Rgba32>(input);
                    image.SetImage(newImage);
                    importedAnyImage = true;
                    importedPaths.Add(replacementPath);
                    AppendLog($"Reimportado: {replacementPath.FullName}");
                }

                if (!importedAnyImage)
                {
                    AppendLog($"Nenhum PNG correspondente foi encontrado para {filePath.FullName}.");
                    return;
                }

                var saveResult = await _fileManager.SaveFile(loadedFile.LoadedFileState);
                if (!saveResult.IsSuccessful)
                {
                    AppendLog($"Failed to save {filePath.FullName}: {saveResult.Reason} {saveResult.Exception?.Message}".Trim());
                    return;
                }

                if (_deleteImportedPngCheckBox.Checked)
                {
                    foreach (var importedPath in importedPaths)
                    {
                        replacementFileSystem.DeleteFile(importedPath);
                        AppendLog($"Apagado: {importedPath.FullName}");
                    }
                }
            }
            finally
            {
                _fileManager.Close(loadedFile.LoadedFileState!);
            }
        }

        private string GetImageFileName(UPath texPath, int imageCount, IImageFile image, int index)
        {
            var texBaseName = SanitizeFileName(texPath.GetNameWithoutExtension());
            if (imageCount <= 1)
                return texBaseName + ".png";

            var imageName = string.IsNullOrWhiteSpace(image.ImageInfo.Name) ? $"{index:00}" : SanitizeFileName(image.ImageInfo.Name);
            return $"{texBaseName}.{imageName}.png";
        }

        private string SanitizeFileName(string name)
        {
            foreach (var invalidFileNameChar in Path.GetInvalidFileNameChars())
                name = name.Replace(invalidFileNameChar, '_');

            return name;
        }

        private void AppendLog(string text)
        {
            var currentText = _logEditor.GetText();
            _logEditor.SetText(currentText + text + Environment.NewLine);
        }
    }
}
