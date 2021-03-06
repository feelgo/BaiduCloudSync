﻿using GlobalUtil;
using GlobalUtil.NetUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace BaiduCloudSync
{
    //Extended functions for Baidu netdisk
    public partial class BaiduPCS
    {
        //关于自己瞎起名的Symbol Link的解释
        // 总所周知（个屁），百度云盘的秒传端口对文件校验有4个参数，其中是两个MD5 1个crc32和1个文件大小
        // 那么，稍加思考就会得到以下的结论： 这个数据已经在网盘上存在了，不需要再上传，只要匹配到文件之后把它放到你的云盘就好了
        // 所以，这里就合理利用以下“这个数据已经存在”的假设，即对网盘的一些热门资源（也就是存放和分享数量比较多的文件）
        // 只需要记住以上几个参数，就相当于自己可以随时随地获取完整的文件了。
        // 虽然该过程会有极少概率丢失数据，但仍不失为一种扩展网盘容量的方法
        //
        // 因此，symbollink文件实质就是一个含有content_length content_md5 content_crc32 slice_md5数据的json文件而已
        // 需要时就可以把文件读取并直接调用秒传接口创建源文件即可

        // WARNING: 对于那些只有自己拥有的数据不建议使用这种方式，否则可能会造成数据丢失，后果本脑洞大开的码农一概不负责

        /// <summary>
        /// 将网盘的文件转换成秒传数据文件（将产生一个原文件名.symbollink的文件）
        /// </summary>
        /// <param name="path">原文件路径（必要）</param>
        /// <param name="dst_path">保存的文件名（可选）</param>
        /// <returns>新的文件信息</returns>
        public ObjectMetadata ConvertToSymbolLink(string path, string dst_path = null)
        {
            if (_enable_function_trace)
                _trace.TraceInfo("BaiduPCS.ConvertToSymbolLink called: string path=" + path + ", string dst_path=" + (dst_path == null ? "null" : dst_path));

            var rst_event = new ManualResetEventSlim();
            var ret = new ObjectMetadata();
            ConvertToSymbolLinkAsync(path, (suc, data, state) =>
            {
                if (suc)
                    ret = data;
                rst_event.Set();
            }, dst_path);
            rst_event.Wait();
            return ret;
        }

        /// <summary>
        /// 将网盘的文件异步转换成秒传数据文件（将产生一个原文件名.symbollink的文件）
        /// </summary>
        /// <param name="path">原文件路径</param>
        /// <param name="callback">回调函数</param>
        /// <param name="dst_path">保存的文件名</param>
        /// <param name="state">附加参数</param>
        public void ConvertToSymbolLinkAsync(string path, ObjectMetaCallback callback = null, string dst_path = null, object state = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (_enable_function_trace)
                    _trace.TraceInfo("BaiduPCS.ConvertToSymbolLinkAsync called: string path=" + path + "ObjectMetaCallback callback=" + callback?.ToString() + ", string dst_path=" + dst_path);
                var url = GetLocateDownloadLink(path);
                if (url.Length == 0)
                {
                    _trace.TraceWarning("Locate url length is zero");
                    callback?.Invoke(false, new ObjectMetadata(), state);
                    return;
                }
                var ns = new NetStream();
                ns.RetryTimes = 3;
                ns.CookieKey = _auth.CookieIdentifier;
                try
                {
                    var buffer = new byte[BUFFER_SIZE];
                    var md5 = new System.Security.Cryptography.MD5CryptoServiceProvider();
                    int rbytes, total = 0;
                    ns.HttpGet(url[0]);
                    var content_length = ns.HTTP_Response.ContentLength;
                    if (content_length < VALIDATE_SIZE)
                    {
                        _trace.TraceWarning("Content_length is too small, exited");
                        callback?.Invoke(false, new ObjectMetadata(), state);
                        return;
                    }

                    var stream = ns.ResponseStream;
                    do
                    {
                        rbytes = stream.Read(buffer, 0, BUFFER_SIZE);
                        rbytes = (int)Math.Min(VALIDATE_SIZE - total, rbytes);
                        md5.TransformBlock(buffer, 0, rbytes, buffer, 0);
                        total += rbytes;
                    } while (rbytes > 0 && total < VALIDATE_SIZE);
                    md5.TransformFinalBlock(buffer, 0, 0);

                    var slice_md5 = util.Hex(md5.Hash);

                    //从response处获取其他参数
                    var content_md5 = ns.HTTP_Response.Headers["Content-MD5"];
                    var content_crc32 = ns.HTTP_Response.Headers["x-bs-meta-crc32"];
                    uint int_crc32 = uint.Parse(content_crc32);
                    content_crc32 = int_crc32.ToString("X2").ToLower();

                    if (string.IsNullOrEmpty(content_crc32) || string.IsNullOrEmpty(content_md5))
                    {
                        _trace.TraceWarning("Empty content_crc32 or content_md5 detected, pls report this status to developer by opening new issue");
                        callback?.Invoke(false, new ObjectMetadata(), state);
                    }

                    //尝试发送rapid upload请求
                    var temp_path = "/BaiduCloudSyncCache/temp-rapid-upload-request-" + Guid.NewGuid().ToString();

                    var rapid_upload_info = RapidUploadRaw(temp_path, (ulong)content_length, content_md5, content_crc32, slice_md5);
                    DeletePath("/BaiduCloudSyncCache");

                    if (string.IsNullOrEmpty(rapid_upload_info.MD5) || rapid_upload_info.FS_ID == 0)
                    {
                        _trace.TraceWarning("Validate check: post rapid upload failed, operation aborted");
                        callback?.Invoke(false, new ObjectMetadata(), state);
                        return;
                    }

                    //rapid upload通过，整合成json格式的文件上传到服务器
                    var json = new JObject();
                    json.Add("content_length", content_length);
                    json.Add("content_md5", content_md5);
                    json.Add("content_crc32", content_crc32);
                    json.Add("slice_md5", slice_md5);
                    var str_json = JsonConvert.SerializeObject(json);
                    var bytes_json = Encoding.UTF8.GetBytes(str_json);
                    var stream_to_write = new MemoryStream();
                    stream_to_write.Write(bytes_json, 0, bytes_json.Length);
                    stream_to_write.Seek(0, SeekOrigin.Begin);

                    if (dst_path == null)
                        dst_path = path + ".symbollink";
                    var file_meta = UploadRaw(stream_to_write, (ulong)bytes_json.Length, dst_path);
                    stream_to_write.Close();
                    callback?.Invoke(true, file_meta, state);
                }
                catch (Exception ex)
                {
                    _trace.TraceError(ex);
                }
                finally
                {
                    ns.Close();
                }
            });
        }
        /// <summary>
        /// 将网盘的秒传数据文件转成原文件（将产生一个去掉末尾.symbollink的文件）
        /// </summary>
        /// <param name="path">symbollink文件路径（必要）</param>
        /// <param name="dst_path">保存的文件名（可选）</param>
        /// <returns>新的文件信息</returns>
        public ObjectMetadata ConvertFromSymbolLink(string path, string dst_path = null)
        {
            if (_enable_function_trace)
                _trace.TraceInfo("BaiduPCS.ConvertFromSymbolLink called: string path=" + path + ", string dst_path=" + (dst_path == null ? "null" : dst_path));

            var rst_event = new ManualResetEventSlim();
            var ret = new ObjectMetadata();
            ConvertFromSymbolLinkAsync(path, (suc, data, state) =>
            {
                if (suc)
                    ret = data;
                rst_event.Set();
            }, dst_path);
            rst_event.Wait();
            return ret;
        }
        /// <summary>
        /// 将网盘的秒传数据文件异步转成原文件（将产生一个去掉末尾.symbollink的文件）
        /// </summary>
        /// <param name="path">原文件路径</param>
        /// <param name="callback">回调函数</param>
        /// <param name="dst_path">保存的文件名</param>
        /// <param name="state">附加参数</param>
        public void ConvertFromSymbolLinkAsync(string path, ObjectMetaCallback callback = null, string dst_path = null, object state = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (_enable_function_trace)
                    _trace.TraceInfo("BaiduPCS.ConvertFromSymbolLinkAsync called: string path=" + path + ", ObjectMetaCallback callback=" + callback?.ToString() + ", string dst_path=" + dst_path);
                var url = GetLocateDownloadLink(path);
                if (url.Length == 0)
                {
                    _trace.TraceWarning("Locate url length is zero");
                    callback?.Invoke(false, new ObjectMetadata(), state);
                    return;
                }

                var ns = new NetStream();
                ns.RetryTimes = 3;
                ns.CookieKey = _auth.CookieIdentifier;
                try
                {
                    ns.HttpGet(url[0]);

                    var response_json = ns.ReadResponseString();
                    var json = JsonConvert.DeserializeObject(response_json) as JObject;

                    var content_length = json.Value<ulong>("content_length");
                    var content_md5 = json.Value<string>("content_md5");
                    var content_crc32 = json.Value<string>("content_crc32");
                    var slice_md5 = json.Value<string>("slice_md5");


                    if (content_length == 0 || string.IsNullOrEmpty(content_md5) || string.IsNullOrEmpty(content_crc32))
                    {
                        callback?.Invoke(false, new ObjectMetadata(), state);
                        return;
                    }

                    if (dst_path == null)
                        dst_path = path.EndsWith(".symbollink") ? path.Substring(0, path.Length - 11) : (path + "." + path.Split('.').Last());

                    var data = RapidUploadRaw(dst_path, content_length, content_md5, content_crc32, slice_md5);
                    callback?.Invoke(true, data, state);
                }
                catch (Exception ex)
                {
                    _trace.TraceError(ex);
                    callback?.Invoke(false, new ObjectMetadata(), state);
                }
                finally
                {
                    ns.Close();
                }
            });
        }
        //文件夹同步
        //#region Folder Sync
        //private bool _get_syncup_data(string local_path, string remote_path, LocalFileCacher local_cacher, FileListCacher remote_cacher, ref List<ObjectMetadata> delete_list, ref List<TrackedData> upload_list, bool recursive)
        //{
        //    var local_files = local_cacher.GetDataFromPath(local_path, false);
        //    var remote_files = remote_cacher.GetFileList(remote_path);

        //    //extract file names only
        //    var local_filenames = new List<string>();
        //    var remote_filenames = new List<string>();
        //    var local_paths = new List<string>();
        //    var remote_paths = new List<string>();

        //    foreach (var item in local_files)
        //    {
        //        if (!item.IsDir)
        //            local_filenames.Add(item.Path.Split('/').Last());
        //        else
        //            local_paths.Add(item.Path.Split('/').Last());
        //    }
        //    foreach (var item in remote_files)
        //    {
        //        if (!item.IsDir)
        //            remote_filenames.Add(item.ServerFileName);
        //        else
        //            remote_paths.Add(item.ServerFileName);
        //    }

        //    //generate differential filename list

        //    //remote_filenames - local_filenames
        //    // the files exist in remote path but do not exist in local path (delete)
        //    var delete_files = new List<string>();
        //    delete_files.AddRange(remote_filenames.Except(local_filenames));

        //    var upload_files = new List<string>();
        //    //remote_filenames ∩ local_filenames
        //    // the files exist in both remote and local (compare and overwrite data)
        //    upload_files.AddRange(remote_filenames.Intersect(local_filenames));

        //    var pure_upload_files = new List<string>();
        //    //local_filenames - remote_filenames
        //    // the files exist in local path but do not exist in remote path (upload)
        //    pure_upload_files.AddRange(local_filenames.Except(remote_filenames));

        //    //converting to data list
        //    var delete_file_data = new List<ObjectMetadata>();
        //    foreach (var item in delete_files)
        //    {
        //        delete_file_data.Add(remote_files.First(o => o.ServerFileName == item)); //o(n^2)
        //    }
        //    var upload_file_data = new List<TrackedData>();
        //    foreach (var item in upload_files)
        //    {
        //        var local_data = local_files.First(o => o.Path.Split('/').Last() == item); //o(n^2)
        //        var remote_data = remote_files.First(o => o.ServerFileName == item);

        //        //skip same files
        //        //if (local_data.ContentSize != remote_data.Size || local_data.MD5 != remote_data.MD5)

        //        //忽略大于2G文件时的md5检查
        //        if (local_data.ContentSize != remote_data.Size || (remote_data.Size < int.MaxValue && local_data.MD5 != remote_data.MD5))
        //        {
        //            upload_file_data.Add(local_data);
        //        }
        //    }
        //    foreach (var item in pure_upload_files)
        //    {
        //        upload_file_data.Add(local_files.First(o => o.Path.Split('/').Last() == item));
        //    }

        //    //appending data
        //    delete_list.AddRange(delete_file_data);
        //    upload_list.AddRange(upload_file_data);



        //    //generate differential path list
        //    //remote_paths - local_paths
        //    // the directories exist in remote path but do not exist in local path (delete)
        //    var delete_paths = new List<string>();
        //    delete_paths.AddRange(remote_paths.Except(local_paths));

        //    //remote_paths ∩ local_paths
        //    // the path exist in both remote and local (not modified)

        //    //local_paths - remote_paths
        //    // the directories exist in local but do not exist in remote path (create new)
        //    var create_paths = new List<string>();
        //    create_paths.AddRange(local_paths.Except(remote_paths));

        //    //converting to data list
        //    var delete_path_data = new List<ObjectMetadata>();
        //    foreach (var item in delete_paths)
        //    {
        //        delete_path_data.Add(remote_files.First(o => o.ServerFileName == item)); //o(n^2)
        //    }
        //    var create_path_data = new List<TrackedData>();
        //    foreach (var item in create_paths)
        //    {
        //        var local_data = local_files.First(o => o.Path.Split('/').Last() == item); //o(n^2)
        //        create_path_data.Add(local_data);
        //    }
        //    //appending data
        //    delete_list.AddRange(delete_path_data);
        //    upload_list.AddRange(create_path_data);

        //    //recursive
        //    if (recursive)
        //    {
        //        foreach (var item in local_files)
        //        {
        //            if (item.IsDir)
        //            {
        //                var name = item.Path.Split('/').Last();
        //                _get_syncup_data(local_path + "/" + name, remote_path + name + "/", local_cacher, remote_cacher, ref delete_list, ref upload_list, recursive);
        //            }
        //        }
        //    }
        //    return true;
        //}
        //public bool GetSyncUpData(string local_path, string remote_path, LocalFileCacher local_cacher, FileListCacher remote_cacher, out List<ObjectMetadata> delete_list, out List<TrackedData> upload_list, bool recursive = true)
        //{
        //    Tracer.GlobalTracer.TraceInfo("BaiduPCS.GetSyncUpData called: string local_path=" + local_path + ", string remote_path=" + remote_path + ", bool recursive=" + recursive);
        //    delete_list = new List<ObjectMetadata>();
        //    upload_list = new List<TrackedData>();
        //    if (local_cacher == null || remote_cacher == null) return false;
        //    if (string.IsNullOrEmpty(local_path) || string.IsNullOrEmpty(remote_path)) return false;

        //    local_path = local_path.Replace(@"\", "/");
        //    remote_path = remote_path.Replace(@"\", "/");

        //    if (!remote_path.EndsWith("/")) remote_path += "/";
        //    if (local_path.EndsWith("/")) local_path = local_path.Substring(0, local_path.Length - 1);

        //    return _get_syncup_data(local_path, remote_path, local_cacher, remote_cacher, ref delete_list, ref upload_list, recursive);
        //}
        //private bool _get_syncdown_data(string local_path, string remote_path, LocalFileCacher local_cacher, FileListCacher remote_cacher, ref List<TrackedData> delete_list, ref List<ObjectMetadata> download_list, bool recursive)
        //{
        //    var local_files = local_cacher.GetDataFromPath(local_path, false);
        //    var remote_files = remote_cacher.GetFileList(remote_path);

        //    var local_filenames = new List<string>();
        //    var remote_filenames = new List<string>();
        //    var local_paths = new List<string>();
        //    var remote_paths = new List<string>();

        //    foreach (var item in local_files)
        //    {
        //        if (item.IsDir)
        //            local_paths.Add(item.Path.Split('/').Last());
        //        else
        //            local_filenames.Add(item.Path.Split('/').Last());
        //    }
        //    foreach (var item in remote_files)
        //    {
        //        if (item.IsDir)
        //            remote_paths.Add(item.ServerFileName);
        //        else
        //            remote_filenames.Add(item.ServerFileName);
        //    }

        //    var delete_files = new List<string>();
        //    delete_files.AddRange(local_filenames.Except(remote_filenames));

        //    var download_files = new List<string>();
        //    download_files.AddRange(local_filenames.Intersect(remote_filenames));

        //    var pure_download_files = new List<string>();
        //    pure_download_files.AddRange(remote_filenames.Except(local_filenames));

        //    var delete_file_data = new List<TrackedData>();
        //    foreach (var item in delete_files)
        //    {
        //        delete_file_data.Add(local_files.First(o => o.Path.Split('/').Last() == item));
        //    }
        //    var download_file_data = new List<ObjectMetadata>();
        //    foreach (var item in download_files)
        //    {
        //        var local_data = local_files.First(o => o.Path.Split('/').Last() == item);
        //        var remote_data = remote_files.First(o => o.ServerFileName == item);
        //        //if (local_data.ContentSize != remote_data.Size || local_data.MD5 != remote_data.MD5)

        //        //忽略大于2G文件时的md5检查
        //        if (local_data.ContentSize != remote_data.Size || (remote_data.Size < int.MaxValue && local_data.MD5 != remote_data.MD5))
        //        {
        //            download_file_data.Add(remote_data);
        //        }
        //    }
        //    foreach (var item in pure_download_files)
        //    {
        //        download_file_data.Add(remote_files.First(o => o.ServerFileName == item));
        //    }

        //    delete_list.AddRange(delete_file_data);
        //    download_list.AddRange(download_file_data);


        //    var delete_paths = new List<string>();
        //    delete_paths.AddRange(local_paths.Except(remote_paths));

        //    var create_paths = new List<string>();
        //    create_paths.AddRange(remote_paths.Except(local_paths));

        //    var delete_path_data = new List<TrackedData>();
        //    foreach (var item in delete_paths)
        //    {
        //        delete_path_data.Add(local_files.First(o => o.Path.Split('/').Last() == item));
        //    }
        //    var create_path_data = new List<ObjectMetadata>();
        //    foreach (var item in create_paths)
        //    {
        //        create_path_data.Add(remote_files.First(o => o.ServerFileName == item));
        //    }

        //    delete_list.AddRange(delete_path_data);
        //    download_list.AddRange(create_path_data);

        //    if (recursive)
        //    {
        //        foreach (var item in remote_files)
        //        {
        //            if (item.IsDir)
        //            {
        //                var name = item.ServerFileName;
        //                _get_syncdown_data(local_path + "/" + name, remote_path + name + "/", local_cacher, remote_cacher, ref delete_list, ref download_list, recursive);
        //            }
        //        }
        //    }
        //    return true;
        //}
        //public bool GetSyncDownData(string local_path, string remote_path, LocalFileCacher local_cacher, FileListCacher remote_cacher, out List<TrackedData> delete_list, out List<ObjectMetadata> download_list, bool recursive = true)
        //{
        //    Tracer.GlobalTracer.TraceInfo("BaiduPCS.GetSyncDownData called: string local_path=" + local_path + ", string remote_path=" + remote_path + ", bool recursive=" + recursive);
        //    download_list = new List<ObjectMetadata>();
        //    delete_list = new List<TrackedData>();
        //    if (local_cacher == null || remote_cacher == null) return false;
        //    if (string.IsNullOrEmpty(local_path) || string.IsNullOrEmpty(remote_path)) return false;

        //    local_path = local_path.Replace(@"\", "/");
        //    remote_path = remote_path.Replace(@"\", "/");

        //    if (!remote_path.EndsWith("/")) remote_path += "/";
        //    if (local_path.EndsWith("/")) local_path = local_path.Substring(0, local_path.Length - 1);

        //    return _get_syncdown_data(local_path, remote_path, local_cacher, remote_cacher, ref delete_list, ref download_list, recursive);
        //}
        //#endregion
    }
}
