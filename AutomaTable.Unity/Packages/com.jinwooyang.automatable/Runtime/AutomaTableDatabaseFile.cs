using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace AutomaTable.Unity
{
    public static class AutomaTableDatabaseFile
    {
        public static async Task<string> PrepareAsync(string relativePath)
        {
            var normalizedPath = NormalizeRelativePath(relativePath);

#if UNITY_ANDROID && !UNITY_EDITOR
            var sourceUrl = Application.streamingAssetsPath.TrimEnd('/') + "/" + normalizedPath;
            var destinationPath = Path.Combine(
                Application.persistentDataPath,
                "AutomaTable",
                normalizedPath.Replace('/', Path.DirectorySeparatorChar));

            using (var request = UnityWebRequest.Get(sourceUrl))
            {
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new IOException(
                        "Failed to read the AutomaTable database from StreamingAssets: " +
                        request.error);
                }

                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.WriteAllBytes(destinationPath, request.downloadHandler.data);
            }

            return destinationPath;
#else
            var databasePath = Path.Combine(
                Application.streamingAssetsPath,
                normalizedPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    "The AutomaTable database was not found in StreamingAssets.",
                    databasePath);
            }

            return databasePath;
#endif
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A relative database path is required.", nameof(relativePath));
            }

            var normalizedPath = relativePath.Replace('\\', '/');
            var segments = normalizedPath.Split('/');
            if (Path.IsPathRooted(relativePath) ||
                normalizedPath.StartsWith("/", StringComparison.Ordinal) ||
                Array.Exists(segments, segment =>
                    segment.Length == 0 ||
                    segment == "." ||
                    segment == ".." ||
                    segment.IndexOf(':') >= 0))
            {
                throw new ArgumentException(
                    "The database path must stay within StreamingAssets.",
                    nameof(relativePath));
            }

            return normalizedPath;
        }
    }
}
