﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using FubarDev.WebDavServer.Model;
using FubarDev.WebDavServer.Properties;

namespace FubarDev.WebDavServer.FileSystem.DotNet
{
    public class DotNetFile : DotNetEntry, IDocument
    {
        public DotNetFile(DotNetFileSystem fileSystem, FileInfo info, Uri path)
            : base(fileSystem, info, path)
        {
            FileInfo = info;
        }

        public FileInfo FileInfo { get; }

        public long Length => FileInfo.Length;

        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream>(FileInfo.OpenRead());
        }

        public Task<Stream> CreateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream>(FileInfo.Open(FileMode.Create, FileAccess.Write));
        }

        public override Task<DeleteResult> DeleteAsync(CancellationToken cancellationToken)
        {
            FileInfo.Delete();
            return Task.FromResult(new DeleteResult(WebDavStatusCodes.OK, null));
        }

        public Task<IEntry> CopyToAsync(ICollection collection, string name, CancellationToken cancellationToken)
        {
            var dir = (DotNetDirectory) collection;
            var targetFileName = System.IO.Path.Combine(dir.DirectoryInfo.FullName, name);
            File.Copy(FileInfo.FullName, targetFileName, true);
            return dir.GetChildAsync(name, cancellationToken);
        }

        public Task<IEntry> MoveToAsync(ICollection collection, string name, CancellationToken cancellationToken)
        {
            var dir = (DotNetDirectory)collection;
            var targetFileName = System.IO.Path.Combine(dir.DirectoryInfo.FullName, name);
            if (File.Exists(targetFileName))
                File.Delete(targetFileName);
            File.Move(FileInfo.FullName, targetFileName);
            return dir.GetChildAsync(name, cancellationToken);
        }

        protected override IEnumerable<IUntypedReadableProperty> GetLiveProperties()
        {
            foreach (var liveProperty in base.GetLiveProperties())
            {
                yield return liveProperty;
            }

            yield return new ContentLengthProperty(ct => Task.FromResult(Length));
            yield return new GetETagProperty(FileSystem.PropertyStore, this, 0);
        }
    }
}
