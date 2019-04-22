using Microsoft.DotNet.ImageBuilder.ViewModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.DotNet.ImageBuilder
{
    internal static class ImageImportExportHelper
    {
        private const string ImageTarFile = "image.tar.gz";

        public static void ExportImages(IEnumerable<TagInfo> tags, string exportPath, bool isDryRun)
        {
            IEnumerable<IGrouping<string, TagInfo>> tagGroups = GroupTagsByDistinctImage(tags);

            Parallel.ForEach(tagGroups, group =>
            {
                var primaryTag = GetPrimaryTag(group);

                string imageExportFolder = GetImageFolderPath(primaryTag.BuildContextPath, exportPath);
                Directory.CreateDirectory(imageExportFolder);
                DockerHelper.SaveImage(
                    primaryTag.FullyQualifiedName,
                    Path.Combine(imageExportFolder, ImageTarFile),
                    isDryRun);
            });
        }

        public static void ImportImages(IEnumerable<TagInfo> tags, string importPath, bool isDryRun)
        {
            IEnumerable<IGrouping<string, TagInfo>> tagGroups = GroupTagsByDistinctImage(tags);

            Parallel.ForEach(tagGroups, tagsByImage =>
            {
                string buildContextPath = tagsByImage.Key;
                string imageImportFolder = GetImageFolderPath(buildContextPath, importPath);
                string imageTarPath = Path.Combine(imageImportFolder, ImageTarFile);

                DockerHelper.LoadImage(imageTarPath, isDryRun);

                var primaryTag = GetPrimaryTag(tagsByImage);

                Parallel.ForEach(tagsByImage.Except(new TagInfo[] { primaryTag }), tag =>
                {
                    DockerHelper.CreateTag(primaryTag.FullyQualifiedName, tag.FullyQualifiedName, isDryRun);
                });
            });
            
        }

        private static TagInfo GetPrimaryTag(IEnumerable<TagInfo> tags)
        {
            return tags.OrderBy(t => t.FullyQualifiedName).First();
        }

        private static IEnumerable<IGrouping<string, TagInfo>> GroupTagsByDistinctImage(IEnumerable<TagInfo> tags)
        {
            return tags
                .Where(tag => !tag.Model.IsLocal)
                .GroupBy(t => t.BuildContextPath);
        }

        private static string GetImageFolderPath(string buildContextPath, string basePath)
        {
            return Path.Combine(basePath, buildContextPath.Replace("/", "\\"));
        }
    }
}
