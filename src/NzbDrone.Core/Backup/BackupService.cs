using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Backup
{
    public interface IBackupService
    {
        void Backup(BackupType backupType);
        List<Backup> GetBackups();
        void Restore(string backupFileName);
        string GetBackupFolder();
        string GetBackupFolder(BackupType backupType);
    }

    public class BackupService : IBackupService, IExecute<BackupCommand>
    {
        private readonly IMainDatabase _maindDb;
        private readonly IMakeDatabaseBackup _makeDatabaseBackup;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IDiskProvider _diskProvider;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IArchiveService _archiveService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public static readonly Regex BackupFileRegex = new Regex(@"(nzbdrone|radarr)_backup_v?[._0-9]+\.zip", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public BackupService(IMainDatabase maindDb,
                             IMakeDatabaseBackup makeDatabaseBackup,
                             IDiskTransferService diskTransferService,
                             IDiskProvider diskProvider,
                             IAppFolderInfo appFolderInfo,
                             IArchiveService archiveService,
                             IConfigService configService,
                             Logger logger)
        {
            _maindDb = maindDb;
            _makeDatabaseBackup = makeDatabaseBackup;
            _diskTransferService = diskTransferService;
            _diskProvider = diskProvider;
            _appFolderInfo = appFolderInfo;
            _archiveService = archiveService;
            _configService = configService;
            _logger = logger;
        }

        public void Backup(BackupType backupType)
        {
            _logger.ProgressInfo("Starting Backup");
            var backupFolder = GetBackupFolder(backupType);
            _diskProvider.EnsureFolder(backupFolder);
            if (!_diskProvider.FolderWritable(backupFolder))
            {
                throw new UnauthorizedAccessException($"Backup folder {backupFolder} is not writable");
            }

            var temporaryPath = StagingPath("backup");
            try
            {
                CreateStagingFolder(temporaryPath);
                var dateNow = DateTime.Now;
                var backupFilename = $"radarr_backup_v{BuildInfo.Version}_{dateNow:yyyy.MM.dd_HH.mm.ss}.zip";
                var backupPath = Path.Combine(backupFolder, backupFilename);
                if (backupType != BackupType.Manual)
                {
                    CleanupOldBackups(backupType);
                }

                BackupConfigFile(temporaryPath);
                BackupDatabase(temporaryPath);
                CreateVersionInfo(dateNow, temporaryPath);
                _logger.ProgressDebug("Creating backup zip");
                _diskProvider.DeleteFile(Path.Combine(temporaryPath, "radarr.db-journal"));
                _archiveService.CreateZip(backupPath, _diskProvider.GetFiles(temporaryPath, false));
                _logger.ProgressDebug("Backup zip created");
            }
            finally
            {
                DeleteStagingFolder(temporaryPath);
            }
        }

        public List<Backup> GetBackups()
        {
            var backups = new List<Backup>();

            foreach (var backupType in Enum.GetValues(typeof(BackupType)).Cast<BackupType>())
            {
                var folder = GetBackupFolder(backupType);

                if (_diskProvider.FolderExists(folder))
                {
                    backups.AddRange(GetBackupFiles(folder).Select(b => new Backup
                    {
                        Name = Path.GetFileName(b),
                        Type = backupType,
                        Size = _diskProvider.GetFileSize(b),
                        Time = _diskProvider.FileGetLastWrite(b)
                    }));
                }
            }

            return backups;
        }

        public void Restore(string backupFileName)
        {
            if (backupFileName.EndsWith(".zip"))
            {
                var restoredFile = false;
                var temporaryPath = StagingPath("backup_restore");
                try
                {
                    CreateStagingFolder(temporaryPath);
                    _archiveService.Extract(backupFileName, temporaryPath);

                    foreach (var file in _diskProvider.GetFiles(temporaryPath, false))
                    {
                        var fileName = Path.GetFileName(file);

                        if (fileName.Equals("Config.xml", StringComparison.InvariantCultureIgnoreCase))
                        {
                            _diskProvider.MoveFile(file, _appFolderInfo.GetConfigPath(), true);
                            restoredFile = true;
                        }

                        if (fileName.Equals("nzbdrone.db", StringComparison.InvariantCultureIgnoreCase))
                        {
                            _diskProvider.MoveFile(file, _appFolderInfo.GetDatabaseRestore(), true);
                            restoredFile = true;
                        }

                        if (fileName.Equals("radarr.db", StringComparison.InvariantCultureIgnoreCase))
                        {
                            _diskProvider.MoveFile(file, _appFolderInfo.GetDatabaseRestore(), true);
                            restoredFile = true;
                        }
                    }

                    if (!restoredFile)
                    {
                        throw new RestoreBackupFailedException(HttpStatusCode.NotFound, "Unable to restore database file from backup");
                    }
                }
                finally
                {
                    DeleteStagingFolder(temporaryPath);
                }

                return;
            }

            _diskProvider.MoveFile(backupFileName, _appFolderInfo.GetDatabaseRestore(), true);
        }

        public string GetBackupFolder()
        {
            var backupFolder = _configService.BackupFolder;

            if (Path.IsPathRooted(backupFolder))
            {
                return backupFolder;
            }

            return Path.Combine(_appFolderInfo.GetAppDataPath(), backupFolder);
        }

        public string GetBackupFolder(BackupType backupType)
        {
            return Path.Combine(GetBackupFolder(), backupType.ToString().ToLower());
        }

        private string StagingPath(string purpose) => Path.Combine(_appFolderInfo.TempFolder, $"radarr_{purpose}_{Guid.NewGuid():N}");

        private void CreateStagingFolder(string path)
        {
            _diskProvider.EnsureFolder(path);
            if (OsInfo.IsNotWindows)
            {
                _diskProvider.SetPermissions(path, "700", null);
            }
        }

        private void DeleteStagingFolder(string path)
        {
            if (_diskProvider.FolderExists(path))
            {
                _diskProvider.DeleteFolder(path, true);
            }
        }

        private void BackupDatabase(string temporaryPath)
        {
            if (_maindDb.DatabaseType == DatabaseType.SQLite)
            {
                _logger.ProgressDebug("Backing up database");

                _makeDatabaseBackup.BackupDatabase(_maindDb, temporaryPath);
            }
        }

        private void BackupConfigFile(string temporaryPath)
        {
            _logger.ProgressDebug("Backing up config.xml");

            var configFile = _appFolderInfo.GetConfigPath();
            var tempConfigFile = Path.Combine(temporaryPath, Path.GetFileName(configFile));

            _diskTransferService.TransferFile(configFile, tempConfigFile, TransferMode.Copy);
        }

        private void CreateVersionInfo(DateTime dateNow, string temporaryPath)
        {
            var tempFile = Path.Combine(temporaryPath, "INFO");

            var builder = new StringBuilder();
            builder.AppendLine($"v{BuildInfo.Version}");
            builder.AppendLine($"{dateNow:yyyy-MM-dd HH:mm:ss}");

            _diskProvider.WriteAllText(tempFile, builder.ToString());
        }

        private void CleanupOldBackups(BackupType backupType)
        {
            var retention = _configService.BackupRetention;

            _logger.Debug("Cleaning up backup files older than {0} days", retention);
            var files = GetBackupFiles(GetBackupFolder(backupType));

            foreach (var file in files)
            {
                var lastWriteTime = _diskProvider.FileGetLastWrite(file);

                if (lastWriteTime.AddDays(retention) < DateTime.UtcNow)
                {
                    _logger.Debug("Deleting old backup file: {0}", file);
                    _diskProvider.DeleteFile(file);
                }
            }

            _logger.Debug("Finished cleaning up old backup files");
        }

        private IEnumerable<string> GetBackupFiles(string path)
        {
            var files = _diskProvider.GetFiles(path, false);

            return files.Where(f => BackupFileRegex.IsMatch(f));
        }

        public void Execute(BackupCommand message)
        {
            Backup(message.Type);
        }
    }
}
