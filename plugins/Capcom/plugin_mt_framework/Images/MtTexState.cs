using Konnect.Contract.DataClasses.FileSystem;
using Konnect.Contract.DataClasses.Plugin.File;
using Konnect.Contract.FileSystem;
using Konnect.Contract.Plugin.File;
using Konnect.Contract.Plugin.File.Image;
using Konnect.Plugin.File.Image;

namespace plugin_mt_framework.Images
{
    class MtTexState : ILoadFiles, ISaveFiles, IImageFilePluginState
    {
        private readonly MtTex _tex = new();
        private List<IImageFile> _images;

        public IReadOnlyList<IImageFile> Images => _images;

        public bool ContentChanged => IsContentChanged();

        public async Task Load(IFileSystem fileSystem, UPath filePath, LoadContext loadContext)
        {
            Stream fileStream = await fileSystem.OpenFileAsync(filePath);

            var platform = await MtTexSupport.DeterminePlatform(fileStream, loadContext.DialogManager);
            var infos = _tex.Load(fileStream, platform);

            _images = [];
            foreach (var info in infos)
            {
                var encodingDefinition = platform == MtTexPlatform.PS3
                    ? MtTexSupport.GetEncodingDefinition(platform, info.ImageFormat)
                    : MtTexSupport.GetEncodingDefinition(platform);

                _images.Add(new ImageFile(info, ShouldLock(platform), encodingDefinition));
            }
        }

        public async Task Save(IFileSystem fileSystem, UPath savePath, SaveContext saveContext)
        {
            Stream fileStream = await fileSystem.OpenFileAsync(savePath, FileMode.Create, FileAccess.Write);
            _tex.Save(fileStream, _images.Select(x => x.ImageInfo).ToArray());
        }

        private bool IsContentChanged()
        {
            return _images.Any(x => x.ImageInfo.ContentChanged);
        }

        private bool ShouldLock(MtTexPlatform platform)
        {
            // Lock transcoding for mobile formats, since the 3 images are linked together
            return platform is MtTexPlatform.Mobile or MtTexPlatform.PS3;
        }
    }
}
