// Wayne Lu
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace github_directory_downloader
{
    class Program
    {
        static async Task Main(string[] args)
        {
            /*
             * Usage:
             * 
             * downloader.exe <url> <save path>
             * downloader.exe "https://github.com/microsoft/Windows-driver-samples/tree/master/audio/simpleaudiosample" "c:\temp"
             */

            //args = new string[] { "https://github.com/microsoft/Windows-driver-samples/tree/master/audio/simpleaudiosample", @"c:\temp" };
            //args = new string[] { "https://github.com/microsoft/Windows-driver-samples/blob/master/audio/simpleaudiosample/Source/Inc/Inc.vcxproj", @"c:\temp" };
            //args = new string[] { "https://github.com/microsoft/Windows-driver-samples", @"F:\byom" };

            if (args.Length < 2)
            {
                Logger.Error("Invalid Github directory url and save path");
                return;
            }

            var url = args[0].Trim(); // Github directory url
            var savePath = args[1].Trim(); // local path

            if (!(url.Length > 0 && savePath.Length > 0))
            {
                Logger.Error("Invalid Github directory url and save path");
                return;
            }

            var downloader = new Downloader(url, savePath);

            if (!await downloader.CheckBranchAsync()) // Check if the branch exists
            {
                Logger.Error("Repo branch does not exist");
                return;
            }

            if (downloader.IsBlob()) // If blob file
            {
                await downloader.DownloadBlobFileAsync();
            }
            else // If tree
            {
                var files = await downloader.GetFilesAsync(); // Get all files

                if (files != null && files.Length > 0)
                {
                    await files.ForEachAsync(c => downloader.DownloadFileTaskAsync(c), (c, t) => { }); // Download
                    Console.WriteLine("done");
                }
            }
        }
    }

    internal class Downloader
    {
        private readonly string _owner, _repo, _path, _savePath;
        private string _dirPath = string.Empty, _branch = string.Empty;

        private const string _github_url = "https://github.com/";
        private const string _github_api_url = "https://api.github.com";
        private const string _github_content_url = "https://raw.githubusercontent.com";
        private const string _github_agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.77 Safari/537.36";

        public Downloader(string url, string savePath)
        {
            var paths = url.Replace(_github_url, "").Trim().Split('/');

            _owner = paths[0];
            _repo = paths[1];
            _branch = "master";
            _path = string.Join("/", paths.Skip(2));
            _savePath = savePath;
        }

        public async Task<BlobInfo[]> GetFilesAsync()
        {
            var url = $"{_github_api_url}/repos/{_owner}/{_repo}/git/trees/{_branch}?recursive=1";
            var json = await GetJsonObjectAsync<dynamic>(url);
            var blobs = new List<BlobInfo>();

            if (json != null && json["tree"] != null)
            {
                var trees = json["tree"] as dynamic[];

                if (trees != null && trees.Length > 0)
                {
                    foreach (var item in trees)
                    {
                        var path = item["path"];
                        var type = item["type"];

                        if (path != null && type != null && path.IndexOf(_dirPath) == 0 && type != "tree")
                        {
                            blobs.Add(new BlobInfo
                            {
                                path = path,
                                url = $"{_github_content_url}/{_owner}/{_repo}/{_branch}/{path}"
                            });
                        }
                    }
                }
            }

            return blobs.ToArray();
        }

        internal async Task DownloadBlobFileAsync()
        {
            await DownloadFileTaskAsync(new BlobInfo
            {
                url = $"{_github_content_url}/{_owner}/{_repo}/{_branch}/{_dirPath}",
                path = _dirPath
            });
        }

        internal async Task<Tuple<string, string, Exception>> DownloadFileTaskAsync(BlobInfo info, int timeOut = 3000)
        {
            try
            {
                var path = Path.Combine(_savePath, info.path);
                var dir = Path.GetDirectoryName(path);

                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var client = new WebClient())
                {
                    TimerCallback timerCallback = c =>
                    {
                        var webClient = (WebClient)c;

                        if (!webClient.IsBusy)
                            return;

                        webClient.CancelAsync();
                    };

                    using (var timer = new Timer(timerCallback, client, timeOut, Timeout.Infinite))
                    {
                        await client.DownloadFileTaskAsync(info.url, path);
                    }

                    Logger.Info($"Download {info.url} successfully");
                    //return new Tuple<string, string, Exception>(info.url, info.path, null);
                }
            }
            catch (Exception)
            {
                //return new Tuple<string, string, Exception>(info.url, null, ex);
            }

            return null;
        }

        internal async Task<bool> CheckBranchAsync()
        {
            var path = _path.Replace("blob/", "").Replace("tree/", "").Trim();

            if (path.Trim().Length <= 0)
                return true;

            var branchs = await GetJsonObjectAsync<List<dynamic>>($"{_github_api_url}/repos/{_owner}/{_repo}/branches");

            if (branchs != null && branchs.Count > 0)
            {
                var branch = branchs.FirstOrDefault(c => c != null && c["name"] != null && path.IndexOf(c["name"]) == 0);

                if (branch != null)
                {
                    _dirPath = path.Replace(branch["name"] + "/", "").Trim();
                    _branch = branch["name"];
                    return true;
                }
            }

            return false;
        }

        internal bool IsBlob()
        {
            return _path.IndexOf("blob/") == 0;
        }

        internal Task<T> GetJsonObjectAsync<T>(string url)
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _github_agent);

            return client.GetAsync(url).ContinueWith(responseTask =>
            {
                return responseTask.Result.Content.ReadAsStringAsync().ContinueWith(jsonTask =>
                {
                    return Json.Parse<T>(jsonTask.Result);
                });
            }).Unwrap();
        }
    }

    internal static class IEnumerableExtensions
    {
        public static Task ForEachAsync<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, Task<TResult>> taskSelector, Action<TSource, TResult> resultProcessor)
        {
            var oneAtATime = new SemaphoreSlim(5, 10);

            return Task.WhenAll(
                from item in source
                select ProcessAsync(item, taskSelector, resultProcessor, oneAtATime));
        }

        private static async Task ProcessAsync<TSource, TResult>(TSource item, Func<TSource, Task<TResult>> taskSelector, Action<TSource, TResult> resultProcessor,
            SemaphoreSlim oneAtATime)
        {
            TResult result = await taskSelector(item);
            await oneAtATime.WaitAsync();

            try
            {
                resultProcessor(item, result);
            }
            finally
            {
                oneAtATime.Release();
            }
        }
    }

    internal static class Json
    {
        public static T Parse<T>(string json)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                return serializer.Deserialize<T>(json);
            }
            catch (Exception)
            {
                return default;
            }
        }
    }

    internal class BlobInfo
    {
        public string path { get; set; }
        public string url { get; set; }
    }

    internal static class Logger
    {
        /// <summary>
        /// Error
        /// </summary>
        /// <param name="msg"></param>
        public static void Error(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(msg);
            RestoreConsole();
        }

        /// <summary>
        /// Info
        /// </summary>
        /// <param name="msg"></param>
        public static void Info(string msg, params object[] args)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(string.Format(msg, args));
            RestoreConsole();
        }

        internal static void RestoreConsole()
        {
            Console.ForegroundColor = ConsoleColor.Gray; // Console default foreground color
        }
    }
}