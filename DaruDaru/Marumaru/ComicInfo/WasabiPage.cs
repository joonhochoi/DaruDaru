using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DaruDaru.Config;
using DaruDaru.Core;
using DaruDaru.Core.Windows;
using HtmlAgilityPack;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json;

namespace DaruDaru.Marumaru.ComicInfo
{
    internal class WasabiPage : Comic
    {
        public WasabiPage(IMainWindow mainWindow, bool fromSearch, bool addNewOnly, string url, string comicName, string comicNoName)
            : base(mainWindow, fromSearch, addNewOnly, url, comicName)
        {
            this.ComicNoName = comicNoName;

            if (ArchiveManager.CheckDownloaded(this.ArchiveCode))
            {
                this.State = MaruComicState.Complete_2_Archived;
                this.m_mainWindow.WakeDownloader();
            }
        }

        private ImageInfomation[] m_images;
        public string FilePath => this.m_cur.SavePath;
        public string FileDir  => this.ComicNoName != null ? Path.Combine(this.FileDir, ReplaceInvalid(this.ComicNoName) + ".zip") : null;

        public string ArchiveCode => GetArchiveCode(this.Url);

        public static string GetArchiveCode(string url)
        {
            var m = Regexes.RegexArchive.Match(url);
            if (!m.Success)
                return null;

            return m.Groups[1].Value;
        }

        private class ImageInfomation
        {
            public int Index;
            public string ImageUrl;
            public string TempPath;
            public string Extension;
        }
        protected override bool GetInfomationPriv(ref int count)
        {
            var lst = new List<ImageInfomation>();

            var baseUri = new Uri(this.Url);

            using (var wc = new WebClientEx())
            {
                var doc = new HtmlDocument();
                
                var success = Retry(() =>
                {
                    wc.Headers.Set(HttpRequestHeader.Referer, this.Url);
                    var body = wc.DownloadString(this.Url);

                    baseUri = wc.ResponseUri ?? baseUri;

                    doc.LoadHtml(body);

                    // 단편으로 다운로드 시작한 경우에만
                    // 그 외의 경우에는 마루마루 페이지에서 설정되어있던 이름으로 사용.
                    if (this.m_fromSearch)
                    {
                        this.ComicName   = ReplcaeHtmlTag(doc.DocumentNode.SelectSingleNode("//span[@class='title-subject']").InnerText).Trim();
                        this.ComicNoName = ReplcaeHtmlTag(doc.DocumentNode.SelectSingleNode("//div[@class='article-title']").Attributes["title"].Value).Trim();
                    }

                    // 암호걸린 파일
                    if (doc.DocumentNode.SelectSingleNode("//div[@class='pass-box']") != null)
                    {
                        lst.Add(null);
                        return true;
                    }

                    var imgs = doc.DocumentNode.SelectNodes("//img[@data-src]");
                    if (imgs != null && imgs.Count > 0)
                    {
                        foreach (var img in imgs)
                            lst.Add(new ImageInfomation
                            {
                                Index = lst.Count + 1,
                                ImageUrl = new Uri(baseUri, img.Attributes["data-src"].Value).AbsoluteUri,
                                TempPath = Path.GetTempFileName()
                            });
                    }
                    else
                    {
                        var galleryTemplate = doc.DocumentNode.SelectSingleNode("//div[@class='gallery-template']");

                        var sig = Uri.EscapeDataString(galleryTemplate.Attributes["data-signature"].Value);
                        var key = Uri.EscapeDataString(galleryTemplate.Attributes["data-key"].Value);

                        wc.Headers.Set(HttpRequestHeader.Referer, this.Url);
                        var jsonDoc = wc.DownloadString($"http://wasabisyrup.com/assets/{this.ArchiveCode}/1.json?signature={sig}&key={key}");

                        var json = JsonConvert.DeserializeObject<Assets>(jsonDoc);

                        if (json.Message != "ok" || json.Status != "ok")
                            return false;

                        foreach (var img in json.Sources)
                            lst.Add(new ImageInfomation
                            {
                                Index = lst.Count + 1,
                                ImageUrl = new Uri(baseUri, img).AbsoluteUri,
                                TempPath = Path.GetTempFileName()
                            });
                    }


                    return true;
                });

                if (!success || lst.Count == 0)
                {
                    this.State = MaruComicState.Error_1_Error;
                    return false;
                }

                if (lst.Count == 1 && lst[0] == null)
                {
                    this.State = MaruComicState.Error_2_Protected;
                    return true;
                }
            }

            this.Url = baseUri.AbsoluteUri;

            this.ProgressValue = 0;
            this.ProgressMaximum = lst.Count;

            this.m_images = lst.ToArray();

            // 다운로드 시작
            this.State = MaruComicState.Working_2_WaitDownload;

            count = 1;

            return true;
        }

        protected override void StartDownloadPriv()
        {
            try
            {
                if (!File.Exists(this.FilePath))
                {
                    if (!Download())
                    {
                        this.State = MaruComicState.Error_1_Error;
                        return;
                    }

                    this.State = MaruComicState.Working_4_Compressing;
                    Compress();
                    this.SpeedOrFileSize = ToEICFormat(new FileInfo(this.FilePath).Length);

                    this.State = MaruComicState.Complete_1_Downloaded;
                }
                else
                    this.State = MaruComicState.Complete_2_Archived;

                ArchiveManager.AddDownloaded(this.ArchiveCode);
            }
            catch (Exception ex)
            {
                this.State = MaruComicState.Error_1_Error;

                CrashReport.Error(ex);
            }
            finally
            {
                if (this.m_images != null)
                {
                    foreach (var file in this.m_images)
                    {
                        try
                        {
                            File.Delete(file.TempPath);
                        }
                        catch
                        {
                        }
                    }

                    this.m_images = null;
                }
            }
        }

        private bool Download()
        {            
            using (var downloadErrorTokenSource = new CancellationTokenSource())
            {
                var startTime = DateTime.Now;
                long downloaded = 0;

                var taskDownload = Task.Factory.StartNew(() =>
                {
                    var po = new ParallelOptions
                    {
                        CancellationToken = downloadErrorTokenSource.Token
                    };

                    Parallel.ForEach(
                        this.m_images,
                        e =>
                        {
                            var succ = Retry(() =>
                            {
                                var req = WebClientEx.AddHeader(WebRequest.Create(e.ImageUrl));
                                if (req is HttpWebRequest hreq)
                                {
                                    hreq.Referer = this.Url;
                                    hreq.AllowWriteStreamBuffering = false;
                                    hreq.AllowReadStreamBuffering = false;
                                }

                                using (var res = req.GetResponse() as HttpWebResponse)
                                using (var resBody = res.GetResponseStream())
                                {
                                    if (res.ContentType.Contains("text/"))
                                    {
                                        // 파일이 없는 경우
                                        e.Extension = null;
                                        return true;
                                    }

                                    using (var fileStream = new FileStream(e.TempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                                    {
                                        var buff = new byte[4096];
                                        int read;

                                        while ((read = resBody.Read(buff, 0, 4096)) > 0)
                                        {
                                            Interlocked.Add(ref downloaded, read);
                                            fileStream.Write(buff, 0, read);
                                        }

                                        fileStream.Flush();

                                        fileStream.Position = 0;
                                        e.Extension = Signatures.GetExtension(fileStream);
                                    }
                                }

                                if (e.Extension != null)
                                    this.IncrementProgress();

                                return e.Extension != null;
                            });

                            if (!succ)
                                downloadErrorTokenSource.Cancel();
                        });

                    return downloadErrorTokenSource.IsCancellationRequested;
                });

                double befSpeed = 0;
                while (!taskDownload.Wait(0))
                {
                    Thread.Sleep(500);

                    befSpeed = (befSpeed + Interlocked.Read(ref downloaded) / (DateTime.Now - startTime).TotalSeconds) / 2;

                    this.SpeedOrFileSize = ToEICFormat(befSpeed, "/s");
                }

                if (downloadErrorTokenSource.IsCancellationRequested)
                    return false;
            }

            this.SpeedOrFileSize = null;

            return true;
        }

        private void Compress()
        {
            var padLength = Math.Min(3, this.m_images.Length.ToString().Length);

            Directory.CreateDirectory(this.FileDir);

            using (var zipFile = new FileStream(this.FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite))
            using (var zipStream = new ZipOutputStream(zipFile))
            {
                zipStream.SetComment(this.Url);
                zipStream.SetLevel(0);

                var buff = new byte[4096];
                int read;
                foreach (var file in this.m_images)
                {
                    if (file.Extension == null)
                        continue;

                    var entry = new ZipEntry(file.Index.ToString().PadLeft(padLength, '0') + file.Extension);

                    zipStream.PutNextEntry(entry);

                    using (var fileStream = File.OpenRead(file.TempPath))
                        while ((read = fileStream.Read(buff, 0, 4096)) > 0)
                            zipStream.Write(buff, 0, read);
                }

                zipFile.Flush();
            }
        }

        private static string ToEICFormat(double spd, string footer = null)
        {
            if (spd == 0)          return "";
            if (spd > 1000 * 1024) return (spd / 1024 / 1024).ToString("##0.0 \" MiB\"") + footer;
            if (spd > 1000       ) return (spd / 1024       ).ToString("##0.0 \" KiB\"") + footer;
                                   return (spd              ).ToString("##0.0 \" B\""  ) + footer;
        }

        private class Assets
        {
            [JsonProperty("message")]
            public string Message { get; set; }

            [JsonProperty("sources")]
            public string[] Sources { get; set; }

            [JsonProperty("status")]
            public string Status { get; set; }
        }
    }
}
