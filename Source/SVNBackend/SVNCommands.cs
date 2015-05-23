// Copyright (c) <2012> <Playdead>
// This file is subject to the MIT License as seen in the trunk of this repository
// Maintained by: <Kristian Kjems> <kristian.kjems+UnityVC@gmail.com>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using CommandLineExecution;

namespace VersionControl.Backend.SVN
{
    using Logging;
    using AssetPathFilters;
    using ComposedString = ComposedSet<string, FilesAndFoldersComposedStringDatabase>;

    public class SVNCommands : MarshalByRefObject, IVersionControlCommands
    {
        internal const string localEditChangeList = "Open Local";
        private string workingDirectory = ".";
        private string userName;
        private string password;
        private bool allowCacheCredentials = false;
        private string versionNumber;
        private readonly StatusDatabase statusDatabase = new StatusDatabase();
        private bool OperationActive { get { return currentExecutingOperation != null; } }
        private CommandLine currentExecutingOperation = null;
        private Thread refreshThread = null;
        private readonly object operationActiveLockToken = new object();
        private readonly object requestQueueLockToken = new object();
        private readonly object statusDatabaseLockToken = new object();
        private readonly List<string> localRequestQueue = new List<string>();
        private readonly List<string> remoteRequestQueue = new List<string>();
        private volatile bool active = false;
        private volatile bool refreshLoopActive = false;
        private volatile bool requestRefreshLoopStop = false;
        private readonly IVersionControlCommands vcc;

        public SVNCommands()
        {
            vcc = new VCCFilteredAssets(this);
            StartRefreshLoop();
            AppDomain.CurrentDomain.DomainUnload += Unload;
            AppDomain.CurrentDomain.ProcessExit += Unload;
        }

        private void Unload(object sender, EventArgs args)
        {
            TerminateRefreshLoop();
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.DomainUnload -= Unload;
            AppDomain.CurrentDomain.ProcessExit -= Unload;
            TerminateRefreshLoop();
        }

        private void RefreshLoop()
        {
            try
            {
                while (!requestRefreshLoopStop)
                {
                    Thread.Sleep(200);
                    if (active && refreshLoopActive) RefreshStatusDatabase();
                }
            }
            catch (ThreadAbortException) { }
            catch (AppDomainUnloadedException) { }
            catch (Exception e)
            {
                D.ThrowException(e);
            }
            if (!requestRefreshLoopStop) RefreshLoop();
        }

        private void StartRefreshLoop()
        {
            if (refreshThread == null)
            {
                refreshThread = new Thread(RefreshLoop);
                refreshThread.Start();
            }
        }

        // This should only be used during termination of the host AppDomain or Process
        private void TerminateRefreshLoop()
        {
            active = false;
            refreshLoopActive = false;
            requestRefreshLoopStop = true;
            if (currentExecutingOperation != null)
            {
                currentExecutingOperation.Dispose();
                currentExecutingOperation = null;
            }
            if (refreshThread != null)
            {
                refreshThread.Abort();
                refreshThread = null;
            }
        }

        public void Start()
        {
            active = true;
        }

        public void Stop()
        {
            active = false;
        }

        public void ActivateRefreshLoop()
        {
            refreshLoopActive = true;
        }

        public void DeactivateRefreshLoop()
        {
            refreshLoopActive = false;
        }


        private void RefreshStatusDatabase()
        {
            List<string> localCopy = null;
            List<string> remoteCopy = null;

            lock (requestQueueLockToken)
            {
                if (localRequestQueue.Count > 0)
                {
                    localCopy = new List<string>(localRequestQueue.Except(remoteRequestQueue).Distinct());
                    localRequestQueue.Clear();
                }
                if (remoteRequestQueue.Count > 0)
                {
                    remoteCopy = new List<string>(remoteRequestQueue.Distinct());
                    remoteRequestQueue.Clear();
                }
            }
            //if (localCopy != null && localCopy.Count > 0) D.Log("Local Status : " + localCopy.Aggregate((a, b) => a + ", " + b));
            //if (remoteCopy != null && remoteCopy.Count > 0) D.Log("Remote Status : " + remoteCopy.Aggregate((a, b) => a + ", " + b));
            if (localCopy != null && localCopy.Count > 0) Status(localCopy, StatusLevel.Local);
            if (remoteCopy != null && remoteCopy.Count > 0) Status(remoteCopy, StatusLevel.Remote);
        }

        public bool IsReady()
        {
            return !OperationActive && active;
        }

        public void SetWorkingDirectory(string workingDirectory)
        {
            this.workingDirectory = workingDirectory;
        }

        public bool SetUserCredentials(string userName, string password, bool cacheCredentials)
        {
            this.userName = userName;
            this.password = password;
            allowCacheCredentials = cacheCredentials;
            string error = CreateSVNCommandLine("log -l 1").Execute().ErrorStr;
            return string.IsNullOrEmpty(error);
        }

        public VersionControlStatus GetAssetStatus(string assetPath)
        {
            assetPath = assetPath.Replace("\\", "/");
            return GetAssetStatus(new ComposedString(assetPath));
        }

        public VersionControlStatus GetAssetStatus(ComposedString assetPath)
        {
            lock (statusDatabaseLockToken)
            {
                return statusDatabase[assetPath];
            }
        }

        public IEnumerable<VersionControlStatus> GetFilteredAssets(Func<VersionControlStatus, bool> filter)
        {
            lock (statusDatabaseLockToken)
            {
                return new List<VersionControlStatus>(statusDatabase.Values.Where(filter));
            }
        }

        public bool Status(StatusLevel statusLevel, DetailLevel detailLevel)
        {
            if (!active) return false;

            string arguments = "status --xml";
            if (statusLevel == StatusLevel.Remote) arguments += " -u";
            if (detailLevel == DetailLevel.Verbose) arguments += " -v";

            CommandLineOutput commandLineOutput;
            using (var svnStatusTask = CreateSVNCommandLine(arguments))
            {
                commandLineOutput = ExecuteOperation(svnStatusTask);
            }

            if (commandLineOutput == null || commandLineOutput.Failed || string.IsNullOrEmpty(commandLineOutput.OutputStr) || !active) return false;
            try
            {
                var db = SVNStatusXMLParser.SVNParseStatusXML(commandLineOutput.OutputStr);
                lock (statusDatabaseLockToken)
                {
                    foreach (var statusIt in db)
                    {
                        var status = statusIt.Value;
                        status.reflectionLevel = statusLevel == StatusLevel.Remote ? VCReflectionLevel.Repository : VCReflectionLevel.Local;
                        statusDatabase[statusIt.Key] = status;
                    }
                }
                lock (requestQueueLockToken)
                {
                    foreach (var assetIt in db.Keys)
                    {
                        if (statusLevel == StatusLevel.Remote) remoteRequestQueue.Remove(assetIt.Compose());
                        localRequestQueue.Remove(assetIt.Compose());
                    }
                }
                OnStatusCompleted();
            }
            catch (XmlException)
            {
                return false;
            }
            return true;
        }

        public bool Status(IEnumerable<string> assets, StatusLevel statusLevel)
        {
            if (!active) return false;
            if (statusLevel == StatusLevel.Previous)
            {
                statusLevel = StatusLevel.Local;
                foreach (var assetIt in assets)
                {
                    if (GetAssetStatus(assetIt).reflectionLevel == VCReflectionLevel.Repository && statusLevel == StatusLevel.Local)
                    {
                        statusLevel = StatusLevel.Remote;
                    }
                }
            }

            if (statusLevel == StatusLevel.Remote) assets = RemoveFilesIfParentFolderInList(assets);
            const int assetsPerStatus = 20;
            if (assets.Count() > assetsPerStatus)
            {
                return Status(assets.Take(assetsPerStatus), statusLevel) && Status(assets.Skip(assetsPerStatus), statusLevel);
            }

            string arguments = "status --xml -q -v ";
            if (statusLevel == StatusLevel.Remote) arguments += "-u ";
            else arguments += " --depth=empty ";
            arguments += ConcatAssetPaths(RemoveWorkingDirectoryFromPath(assets));

            SetPending(assets);

            CommandLineOutput commandLineOutput;
            using (var svnStatusTask = CreateSVNCommandLine(arguments))
            {
                commandLineOutput = ExecuteOperation(svnStatusTask);
            }
            if (commandLineOutput == null || commandLineOutput.Failed || string.IsNullOrEmpty(commandLineOutput.OutputStr) || !active) return false;
            try
            {
                var db = SVNStatusXMLParser.SVNParseStatusXML(commandLineOutput.OutputStr);
                lock (statusDatabaseLockToken)
                {
                    foreach (var statusIt in db)
                    {
                        var status = statusIt.Value;
                        status.reflectionLevel = statusLevel == StatusLevel.Remote ? VCReflectionLevel.Repository : VCReflectionLevel.Local;
                        statusDatabase[statusIt.Key] = status;
                    }
                }
                lock (requestQueueLockToken)
                {
                    foreach (var assetIt in db.Keys)
                    {
                        if (statusLevel == StatusLevel.Remote) remoteRequestQueue.Remove(assetIt.Compose());
                        localRequestQueue.Remove(assetIt.Compose());
                    }
                }
                OnStatusCompleted();
            }
            catch (XmlException e)
            {
                D.ThrowException(e);
                return false;
            }
            return true;
        }

        private CommandLine CreateSVNCommandLine(string arguments)
        {
            arguments = "--non-interactive " + arguments;
            if (!string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password))
            {
                arguments = " --username " + userName + " --password " + password + (allowCacheCredentials ? " " : " --no-auth-cache ") + arguments;
            }
            return new CommandLine("svn", arguments, workingDirectory);
        }

        private bool CreateOperation(string arguments, bool onlyRunWhenActive = true)
        {
            if (!active && onlyRunWhenActive) return false;

            CommandLineOutput commandLineOutput;
            using (var commandLineOperation = CreateSVNCommandLine(arguments))
            {
                commandLineOperation.OutputReceived += OnProgressInformation;
                commandLineOperation.ErrorReceived += OnProgressInformation;
                commandLineOutput = ExecuteOperation(commandLineOperation);
            }
            return !(commandLineOutput == null || commandLineOutput.Failed);
        }

        private CommandLineOutput ExecuteCommandLine(CommandLine commandLine)
        {
            CommandLineOutput commandLineOutput;
            try
            {
                D.Log(commandLine.ToString());
                currentExecutingOperation = commandLine;
                //System.Threading.Thread.Sleep(500); // emulate latency to SVN server
                commandLineOutput = commandLine.Execute();
            }
            catch (Exception e)
            {
                if (e.StackTrace.Contains("System.IO.MonoSyncFileStream/ReadDelegate"))
                {
                    throw new VCMonoDebuggerAttachedException(e.Message, commandLine.ToString(), e);
                }
                throw new VCCriticalException(e.Message, commandLine.ToString(), e);
            }
            finally
            {
                currentExecutingOperation = null;
            }
            return commandLineOutput;
        }

        private CommandLineOutput ExecuteOperation(CommandLine commandLine, bool useOperationLock = true)
        {
            CommandLineOutput commandLineOutput;
            if (useOperationLock)
            {
                lock (operationActiveLockToken)
                {
                    commandLineOutput = ExecuteCommandLine(commandLine);
                }
            }
            else
            {
                commandLineOutput = ExecuteCommandLine(commandLine);
            }

            //if (commandLineOutput.Arguments.Contains("ExceptionTest.txt"))
            //{
            //    throw new VCCriticalException("Test Exception cast due to ExceptionTest.txt being a part of arguments", commandLine.ToString());
            //}
            if (!string.IsNullOrEmpty(commandLineOutput.ErrorStr))
            {
                var errStr = commandLineOutput.ErrorStr;
                if (errStr.Contains("E170001") || errStr.Contains("get username or password"))
                    throw new VCMissingCredentialsException(errStr, commandLine.ToString());
                if (errStr.Contains("W160042") || errStr.Contains("Newer Version"))
                    throw new VCNewerVersionException(errStr, commandLine.ToString());
                if (errStr.Contains("W155007") || errStr.Contains("'" + workingDirectory + "'" + " is not a working copy"))
                    throw new VCCriticalException(errStr, commandLine.ToString());
                if (errStr.Contains("E720005") || errStr.Contains("Access is denied"))
                    throw new VCCriticalException(errStr, commandLine.ToString());
                if (errStr.Contains("E160028") || errStr.Contains("is out of date"))
                    throw new VCOutOfDate(errStr, commandLine.ToString());
                if (errStr.Contains("E155037") || errStr.Contains("E155004") || errStr.Contains("run 'svn cleanup'") || errStr.Contains("run 'cleanup'"))
                    throw new VCLocalCopyLockedException(errStr, commandLine.ToString());
                if (errStr.Contains("W160035") || errStr.Contains("is already locked by user"))
                    throw new VCLockedByOther(errStr, commandLine.ToString());
                if (errStr.Contains("E730060") || errStr.Contains("Unable to connect") || errStr.Contains("is unreachable") || errStr.Contains("Operation timed out") || errStr.Contains("Can't connect to"))
                    throw new VCConnectionTimeoutException(errStr, commandLine.ToString());

                throw new VCException(errStr, commandLine.ToString());
            }
            return commandLineOutput;
        }

        private bool CreateAssetOperation(string arguments, IEnumerable<string> assets)
        {
            if (assets == null || !assets.Any()) return true;
            return CreateOperation(arguments + ConcatAssetPaths(assets)) && RequestStatus(assets, StatusLevel.Previous);
        }

        private static string FixAtChar(string asset)
        {
            return asset.Contains("@") ? asset + "@" : asset;
        }

        private static string ReplaceCommentChar(string commitMessage)
        {
            return commitMessage.Replace('"', '\'');
        }

        private static string UnifyLineEndingsChar(string commitMessage)
        {
            return commitMessage.Replace("\r\n", "\n");
        }

        private IEnumerable<string> RemoveWorkingDirectoryFromPath(IEnumerable<string> assets)
        {
            return assets.Select(a => a.Replace(workingDirectory, ""));
        }

        private static string PrepareAssetPath(string assetpath)
        {
            return FixAtChar(assetpath.Replace("\\", "/"));
        }

        private static string ConcatAssetPaths(IEnumerable<string> assets)
        {
            assets = assets.Select(PrepareAssetPath);
            if (assets.Any()) return " \"" + assets.Aggregate((i, j) => i + "\" \"" + j) + "\"";
            return "";
        }

        private void SetPending(IEnumerable<string> assets)
        {
            lock (statusDatabaseLockToken)
            {
                foreach (var assetIt in assets)
                {
                    if (GetAssetStatus(assetIt).reflectionLevel != VCReflectionLevel.Pending)
                    {
                        var status = statusDatabase[assetIt];
                        status.reflectionLevel = VCReflectionLevel.Pending;
                        statusDatabase[assetIt] = status;
                    }
                }
                //D.Log("Set Pending : " + assets.Aggregate((a, b) => a + ", " + b));
            }
        }

        private void AddToRemoteStatusQueue(string asset)
        {
            //D.Log("Remote Req : " + asset);
            if (!remoteRequestQueue.Contains(asset)) remoteRequestQueue.Add(asset);
        }

        private void AddToLocalStatusQueue(string asset)
        {
            //D.Log("Local Req : " + asset);
            if (!localRequestQueue.Contains(asset)) localRequestQueue.Add(asset);
        }

        public virtual bool RequestStatus(IEnumerable<string> assets, StatusLevel statusLevel)
        {
            if (assets == null || assets.Count() == 0) return true;

            lock (requestQueueLockToken)
            {
                foreach (string assetIt in assets)
                {
                    var currentReflectionLevel = GetAssetStatus(assetIt).reflectionLevel;
                    if (currentReflectionLevel == VCReflectionLevel.Pending) continue;
                    if (statusLevel == StatusLevel.Remote)
                    {
                        AddToRemoteStatusQueue(assetIt);
                    }
                    else if (statusLevel == StatusLevel.Local)
                    {
                        AddToLocalStatusQueue(assetIt);
                    }
                    else if (statusLevel == StatusLevel.Previous)
                    {
                        if (currentReflectionLevel == VCReflectionLevel.Repository) AddToRemoteStatusQueue(assetIt);
                        else if (currentReflectionLevel == VCReflectionLevel.Local) AddToLocalStatusQueue(assetIt);
                        else if (currentReflectionLevel == VCReflectionLevel.None) AddToLocalStatusQueue(assetIt);
                        else D.LogWarning("Unhandled previous state");
                    }
                }
            }
            SetPending(assets);
            return true;
        }

        public bool Update(IEnumerable<string> assets = null)
        {
            if (assets == null || !assets.Any()) assets = new[] { workingDirectory };
            return CreateAssetOperation("update --force", assets);
        }

        public bool Commit(IEnumerable<string> assets, string commitMessage = "")
        {
            return CreateAssetOperation("commit -m \"" + UnifyLineEndingsChar(ReplaceCommentChar(commitMessage)) + "\"", assets);
        }

        public bool Add(IEnumerable<string> assets)
        {
            return CreateAssetOperation("add", assets);
        }

        public bool Revert(IEnumerable<string> assets)
        {
            bool revertSuccess = CreateAssetOperation("revert --depth=infinity", assets);
            Status(assets, StatusLevel.Previous);
            bool changeListRemoveSuccess = vcc.ChangeListRemove(assets);
            bool releaseSuccess = true;
            if (revertSuccess) releaseSuccess = vcc.ReleaseLock(assets);
            return (revertSuccess && releaseSuccess) || changeListRemoveSuccess;
        }

        public bool Delete(IEnumerable<string> assets, OperationMode mode)
        {
            return CreateAssetOperation("delete" + (mode == OperationMode.Force ? " --force" : ""), assets);
        }

        public bool GetLock(IEnumerable<string> assets, OperationMode mode)
        {
            bool getLockSuccess = CreateAssetOperation("lock" + (mode == OperationMode.Force ? " --force" : ""), assets);
            vcc.ChangeListRemove(assets);
            return getLockSuccess;
        }

        public bool ReleaseLock(IEnumerable<string> assets)
        {
            return CreateAssetOperation("unlock", assets);
        }

        public bool ChangeListAdd(IEnumerable<string> assets, string changelist)
        {
            return CreateAssetOperation("changelist \"" + changelist + "\"", assets) && Status(assets, StatusLevel.Previous);
        }

        public bool ChangeListRemove(IEnumerable<string> assets)
        {
            return CreateAssetOperation("changelist --remove", assets);
        }

        public bool Checkout(string url, string path = "")
        {
            return CreateOperation("checkout \"" + url + "\" \"" + (path == "" ? workingDirectory : path) + "\"");
        }

        public bool AllowLocalEdit(IEnumerable<string> assets)
        {
            return ChangeListAdd(assets, localEditChangeList);
        }

        public bool Resolve(IEnumerable<string> assets, ConflictResolution conflictResolution)
        {
            if (conflictResolution == ConflictResolution.Ignore) return true;
            string conflictparameter = conflictResolution == ConflictResolution.Theirs ? "--accept theirs-full" : "--accept mine-full";
            return CreateAssetOperation("resolve " + conflictparameter, assets);
        }

        public bool Move(string from, string to)
        {
            to = PrepareAssetPath(to);
            from = PrepareAssetPath(from);
            return CreateOperation("move \"" + from + "\" \"" + to + "\"") && RequestStatus(new[] { from, to }, StatusLevel.Previous);
        }

        public bool SetIgnore(string path, IEnumerable<string> assets)
        {
            bool result = CreateOperation(string.Format("propset svn:ignore \"{0}\" {1}", assets.Aggregate((a, b) => a + "\n" + b), path));
            if (result)
            {
                result = CreateAssetOperation(string.Format("commit --depth empty -m \"UVC setting svn:ignore for : {0}\"", assets.Aggregate((a, b) => a + ", " + b)), new[] { path });
            }
            ClearDatabase();
            Status(StatusLevel.Previous, DetailLevel.Normal);
            return result;
        }

        public IEnumerable<string> GetIgnore(string path)
        {
            IEnumerable<string> ignores = null;
            using (var commandLineOperation = CreateSVNCommandLine(string.Format("propget svn:ignore \"{0}\"", path)))
            {
                var commandLineOutput = ExecuteOperation(commandLineOperation);
                if (!commandLineOutput.Failed)
                {
                    ignores = commandLineOutput.OutputStr
                        .Split('\n')
                        .Select(ignore => ignore.Trim('\r', '\n', '\t', ' '))
                        .Distinct()
                        .Where(ignore => !string.IsNullOrEmpty(ignore))
                        .ToArray();
                }
            }
            return ignores;
        }

        public string GetRevision()
        {
            var svnInfo = CreateSVNCommandLine("info --xml ").Execute();
            if (!svnInfo.Failed)
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(svnInfo.OutputStr);
                var revisionNode = xmlDoc.GetElementsByTagName("entry").Item(0);
                if (revisionNode != null)
                {
                    var revision = revisionNode.Attributes["revision"];
                    if(revision != null)
                        return revision.InnerText;
                }
            }
            return null;
        }

        public string GetBasePath(string assetPath)
        {
            assetPath = PrepareAssetPath(assetPath);
            if (string.IsNullOrEmpty(versionNumber))
            {
                versionNumber = CreateSVNCommandLine("--version --quiet").Execute().OutputStr;
            }
            if (versionNumber.StartsWith("1.6") || versionNumber.StartsWith("1.5"))
            {
                return Path.GetDirectoryName(assetPath) + "/.svn/text-base/" + Path.GetFileName(assetPath) + ".svn-base";
            }
            else
            {
                var svnInfo = CreateSVNCommandLine("info --xml " + assetPath).Execute();
                if (!svnInfo.Failed)
                {
                    var xmlDoc = new XmlDocument();
                    xmlDoc.LoadXml(svnInfo.OutputStr);
                    var checksumNode = xmlDoc.GetElementsByTagName("checksum").Item(0);
                    var rootPathNode = xmlDoc.GetElementsByTagName("wcroot-abspath").Item(0);

                    if (checksumNode != null && rootPathNode != null)
                    {
                        string checksum = checksumNode.InnerText;
                        string firstTwo = checksum.Substring(0, 2);
                        string rootPath = rootPathNode.InnerText;
                        string basePath = rootPath + "/.svn/pristine/" + firstTwo + "/" + checksum + ".svn-base";
                        if (File.Exists(basePath)) return basePath;
                    }
                }
            }
            return "";
        }

        public bool GetConflict(string assetPath, out string basePath, out string mine, out string theirs)
        {
            string[] conflictingFiles = Directory.GetFiles(Path.GetDirectoryName(assetPath), Path.GetFileName(assetPath) + ".r*").Where(a => Path.GetExtension(a).StartsWith(".r")).ToArray();
            string minePath = assetPath + ".mine";

            D.Log(string.Format("mine:{0}, theirs:{1}, base:{2}, length:{3}", minePath, conflictingFiles[1], conflictingFiles[0], conflictingFiles.Length));

            if (conflictingFiles.Length == 2 && File.Exists(minePath) && File.Exists(conflictingFiles[0]) && File.Exists(conflictingFiles[1]))
            {
                basePath = conflictingFiles[0];
                mine = minePath;
                theirs = conflictingFiles[1];
                return true;
            }

            basePath = null;
            mine = null;
            theirs = null;
            return false;
        }

        public bool HasValidLocalCopy()
        {
            string error = CreateSVNCommandLine("info").Execute().ErrorStr;
            if (error.Contains("E155037") || error.Contains("E155004") || error.Contains("run 'svn cleanup'") || error.Contains("run 'cleanup'"))
            {
                CreateOperation("cleanup", onlyRunWhenActive: false);
                error = CreateSVNCommandLine("info").Execute().ErrorStr;
            }
            if (!string.IsNullOrEmpty(error))
            {
                D.LogWarning(error);
                return false;
            }
            return true;
        }

        public bool CleanUp()
        {
            return CreateOperation("cleanup");
        }

        public void ClearDatabase()
        {
            lock (statusDatabaseLockToken)
            {
                statusDatabase.Clear();
            }
        }

        public void RemoveFromDatabase(IEnumerable<string> assets)
        {
            D.Log("Remove from DB: "+ assets.Aggregate((a,b) => a + ", " + b));
            lock (statusDatabaseLockToken)
            {
                foreach (var assetIt in assets)
                {
                    statusDatabase.Remove(assetIt);
                }
            }
        }

        IEnumerable<string> RemoveFilesIfParentFolderInList(IEnumerable<string> assets)
        {
            var folders = assets.Where(a => Directory.Exists(a));
            assets = assets.Where(a => !folders.Any(f => a.StartsWith(f) && a != f));
            return assets.ToArray();
        }

        public event Action<string> ProgressInformation;
        private void OnProgressInformation(string info)
        {
            if (ProgressInformation != null) ProgressInformation(info);
        }

        public event Action StatusCompleted;
        private void OnStatusCompleted()
        {
            //D.Log("DB Size : " + statusDatabase.Keys.Count); // + "\n" + statusDatabase.Keys.Aggregate((a,b) => a + ", " + b)
            if (StatusCompleted != null) StatusCompleted();
        }
    }
}
