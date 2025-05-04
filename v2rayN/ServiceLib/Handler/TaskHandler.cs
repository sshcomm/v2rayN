namespace ServiceLib.Handler;

public class TaskHandler
{
    private static readonly Lazy<TaskHandler> _instance = new(() => new());
    public static TaskHandler Instance => _instance.Value;
    // Add a list to track downloaded files
    private readonly List<string> _downloadedCoreFiles = new();
    private readonly Dictionary<string, string> _coreTypeMap = new();

    public void RegUpdateTask(Config config, Action<bool, string> updateFunc)
    {
        Task.Run(() => ScheduledTasks(config, updateFunc));
        Task.Run(() => UpdateTaskRunGeo(config, 1, updateFunc));
        Task.Run(() => UpdateTaskRunCore(config, updateFunc));
        Task.Run(() => UpdateTaskRunGui(config, updateFunc));
    }

    private async Task ScheduledTasks(Config config, Action<bool, string> updateFunc)
    {
        Logging.SaveLog("Setup Scheduled Tasks");

        var numOfExecuted = 1;
        while (true)
        {
            //1 minute
            await Task.Delay(1000 * 3600);

            //Execute once 1 minute
            await UpdateTaskRunSubscription(config, updateFunc);

            //Execute once 20 minute
            if (numOfExecuted % 20 == 0)
            {
                //Logging.SaveLog("Execute save config");

                await ConfigHandler.SaveConfig(config);
                await ProfileExHandler.Instance.SaveTo();
            }

            //Execute once 1 hour
            if (numOfExecuted % 60 == 0)
            {
                //Logging.SaveLog("Execute delete expired files");

                FileManager.DeleteExpiredFiles(Utils.GetBinConfigPath(), DateTime.Now.AddHours(-1));
                FileManager.DeleteExpiredFiles(Utils.GetLogPath(), DateTime.Now.AddMonths(-1));
                FileManager.DeleteExpiredFiles(Utils.GetTempPath(), DateTime.Now.AddMonths(-1));

                //Check once 1 hour
                await UpdateTaskRunGeo(config, numOfExecuted / 60, updateFunc);
            }

            numOfExecuted++;
        }
    }

    private async Task UpdateTaskRunSubscription(Config config, Action<bool, string> updateFunc)
    {
        var updateTime = ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds();
        var lstSubs = (await AppHandler.Instance.SubItems())?
            .Where(t => t.AutoUpdateInterval > 0)
            .Where(t => updateTime - t.UpdateTime >= t.AutoUpdateInterval * 60)
            .ToList();

        if (lstSubs is not { Count: > 0 })
        {
            return;
        }

        Logging.SaveLog("Execute update subscription");
        var updateHandle = new UpdateService();

        foreach (var item in lstSubs)
        {
            await updateHandle.UpdateSubscriptionProcess(config, item.Id, true, (bool success, string msg) =>
            {
                updateFunc?.Invoke(success, msg);
                if (success)
                {
                    Logging.SaveLog($"Update subscription end. {msg}");
                }
            });
            item.UpdateTime = updateTime;
            await ConfigHandler.AddSubItem(config, item);
            await Task.Delay(1000);
        }
    }

    private async Task UpdateTaskRunGeo(Config config, int hours, Action<bool, string> updateFunc)
        {
            var autoUpdateGeoTime = DateTime.Now;

            Logging.SaveLog("UpdateTaskRunGeo");

            var updateHandle = new UpdateService();
            while (true)
            {
                await Task.Delay(1000 * 3600);

                var dtNow = DateTime.Now;
                if (config.GuiItem.AutoUpdateInterval > 0)
                {
                    if ((dtNow - autoUpdateGeoTime).Hours % config.GuiItem.AutoUpdateInterval == 0)
                    {
                        await updateHandle.UpdateGeoFileAll(config, (bool success, string msg) =>
                        {
                            updateFunc?.Invoke(false, msg);
                        });
                        autoUpdateGeoTime = dtNow;
                    }
                }
            }
        }

    private async Task UpdateTaskRunCore(Config config, Action<bool, string> updateFunc)
    {
        var autoUpdateCoreTime = DateTime.Now;

        Logging.SaveLog("UpdateTaskRunCore");

        var updateHandle = new UpdateService();
        while (true)
        {
            await Task.Delay(1000 * 3600);

            var dtNow = DateTime.Now;
            if (config.GuiItem.AutoUpdateCoreInterval > 0)
            {
                if ((dtNow - autoUpdateCoreTime).Hours % config.GuiItem.AutoUpdateCoreInterval == 0)
                {
                    // Clear previous lists before new update cycle
                    _downloadedCoreFiles.Clear();
                    _coreTypeMap.Clear();

                    // Xray core update
                    await updateHandle.CheckUpdateCore(ECoreType.Xray, config, (bool success, string msg) =>
                    {
                        updateFunc?.Invoke(false, msg);
                        if (success)
                        {
                            // Store the downloaded file path and associate it with the core type
                            _downloadedCoreFiles.Add(msg);
                            _coreTypeMap[msg] = ECoreType.Xray.ToString();
                            Logging.SaveLog($"Download Xray core: {msg}");
                        }
                    }, false);

                    // sing-box core update
                    await updateHandle.CheckUpdateCore(ECoreType.sing_box, config, (bool success, string msg) =>
                    {
                        updateFunc?.Invoke(false, msg);
                        if (success)
                        {
                            // Store the downloaded file path and associate it with the core type
                            _downloadedCoreFiles.Add(msg);
                            _coreTypeMap[msg] = ECoreType.sing_box.ToString();
                            Logging.SaveLog($"Download sing-box core: {msg}");
                        }
                    }, false);

                    // mihomo core update
                    await updateHandle.CheckUpdateCore(ECoreType.mihomo, config, (bool success, string msg) =>
                    {
                        updateFunc?.Invoke(false, msg);
                        if (success)
                        {
                            // Store the downloaded file path and associate it with the core type
                            _downloadedCoreFiles.Add(msg);
                            _coreTypeMap[msg] = ECoreType.mihomo.ToString();
                            Logging.SaveLog($"Download mihomo core: {msg}");
                        }
                    }, false);

                    // Process downloaded files - move them from guitemp to bin
                    await ProcessDownloadedCoreFiles(updateFunc);

                    autoUpdateCoreTime = dtNow;
                }
            }
        }
    }

    // New method to process downloaded files
    private async Task ProcessDownloadedCoreFiles(Action<bool, string> updateFunc)
    {
        foreach (var fileName in _downloadedCoreFiles)
        {
            if (string.IsNullOrEmpty(fileName) || !File.Exists(fileName))
            {
                continue;
            }

            try
            {
                // Get the core type for this file
                if (!_coreTypeMap.TryGetValue(fileName, out var coreType))
                {
                    continue;
                }

                var toPath = Utils.GetBinPath("", coreType);
                Logging.SaveLog($"Processing downloaded file: {fileName} to {toPath}");

                // Extract based on file type
                if (fileName.Contains(".tar.gz"))
                {
                    FileManager.DecompressTarFile(fileName, toPath);
                    var dir = new DirectoryInfo(toPath);
                    if (dir.Exists)
                    {
                        foreach (var subDir in dir.GetDirectories())
                        {
                            FileManager.CopyDirectory(subDir.FullName, toPath, false, true);
                            subDir.Delete(true);
                        }
                    }
                }
                else if (fileName.Contains(".gz"))
                {
                    FileManager.DecompressFile(fileName, toPath, coreType);
                }
                else
                {
                    FileManager.ZipExtractToFile(fileName, toPath, "geo");
                }

                // Set executable permissions on Linux/macOS
                if (Utils.IsNonWindows())
                {
                    var filesList = (new DirectoryInfo(toPath)).GetFiles().Select(u => u.FullName).ToList();
                    foreach (var file in filesList)
                    {
                        await Utils.SetLinuxChmod(Path.Combine(toPath, coreType.ToLower()));
                    }
                }

                updateFunc?.Invoke(false, $"Updated {coreType} successfully");

                // Delete the temp file
                if (File.Exists(fileName))
                {
                    File.Delete(fileName);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"Error processing downloaded file {fileName}: {ex.Message}");
                updateFunc?.Invoke(false, $"Error updating core: {ex.Message}");
            }
        }

        // Clear the lists after processing
        _downloadedCoreFiles.Clear();
        _coreTypeMap.Clear();
    }

    private async Task UpdateTaskRunGui(Config config, Action<bool, string> updateFunc)
    {
        var autoUpdateGuiTime = DateTime.Now;

        Logging.SaveLog("UpdateTaskRunGui");

        var updateHandle = new UpdateService();
        while (true)
        {
            await Task.Delay(1000 * 3600);

            var dtNow = DateTime.Now;
            if (config.GuiItem.AutoUpdateCoreInterval > 0)
            {
                if ((dtNow - autoUpdateGuiTime).Hours % config.GuiItem.AutoUpdateCoreInterval == 0)
                {
                    await updateHandle.CheckUpdateGuiN(config, (bool success, string msg) =>
                    {
                        updateFunc?.Invoke(success, msg);
                    }, false);
                    autoUpdateGuiTime = dtNow;
                }
            }
        }
    }    
        
}
