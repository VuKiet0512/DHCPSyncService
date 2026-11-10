using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace DHCPSyncService
{
    public partial class Service1 : ServiceBase
    {
        private Timer timer;

        private readonly string[] TargetScopes = new string[] { "10.10.30.0", "10.10.70.0", "10.10.60.0", "10.10.20.0" };

        public Service1()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                if (!Directory.Exists(@"C:\Logs"))
                {
                    Directory.CreateDirectory(@"C:\Logs");
                }
                WriteLog("=================== Khởi động DHCPSyncService ===================");
            }
            catch (Exception ex)
            {
                EventLog.WriteEntry("DHCPSyncService", "Không thể tạo thư mục Log: " + ex.Message, EventLogEntryType.Error);
            }

            timer = new Timer();
            timer.Interval = 5 * 60 * 1000;   // 5 phút
            timer.Elapsed += Timer_Elapsed;
            timer.AutoReset = false;
            timer.Start();

            Task.Run(() =>
            {
                try
                {
                    SyncDHCP(TargetScopes);
                }
                catch (Exception ex)
                {
                    WriteLog("Lỗi trong luồng khởi chạy ban đầu: " + ex.Message);
                }
                finally
                {
                    timer.Start();
                }
            });
        }

        protected override void OnStop()
        {
            if (timer != null)
            {
                timer.Stop();
                timer.Dispose();
            }
            WriteLog("DHCPSyncService đã dừng.");
        }

        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                SyncDHCP(TargetScopes);
            }
            finally
            {
                timer.Start();
            }
        }

        private string RunPowerShell(string command)
        {
            using (Process p = new Process())
            {
                p.StartInfo.FileName = "powershell.exe";
                p.StartInfo.Arguments = "-ExecutionPolicy Bypass -Command \"" + command + "\"";

                p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                p.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;

                p.Start();

                string result = p.StandardOutput.ReadToEnd();
                string error = p.StandardError.ReadToEnd();

                p.WaitForExit();

                if (!string.IsNullOrEmpty(error))
                {
                    WriteLog("LỖI POWERSHELL: " + error.Trim());
                }

                return result;
            }
        }

        private void SyncDHCP(params string[] scopeIds)
        {
            try
            {
                if (scopeIds == null || scopeIds.Length == 0)
                {
                    WriteLog("Không có Scope ID nào được truyền vào để quét.");
                    return;
                }

                WriteLog($"Bắt đầu quét danh sách DHCP Leases của {scopeIds.Length} Scopes chỉ định: {string.Join(", ", scopeIds)}...");

                string utf8Config = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; ";
                List<tblDHCPClient> allClients = new List<tblDHCPClient>();

                foreach (string scopeId in scopeIds)
                {
                    if (string.IsNullOrWhiteSpace(scopeId)) continue;

                    try
                    {
                        WriteLog($"[-] Đang quét dữ liệu của Scope: {scopeId.Trim()}...");

                        string cmdLeases = utf8Config +
                     $"$data = @(Get-DhcpServerv4Lease -ScopeId {scopeId.Trim()} | Select-Object " +
                     "@{Name='IPAddress';Expression={$_.IPAddress.IPAddressToString}}, " +
                     "@{Name='MACAddress';Expression={$_.ClientId}}, " +
                     "@{Name='LeaseExpiryTime';Expression={$_.LeaseExpiryTime.DateTime}}, " +
                     "HostName, AddressState, Description); " +
                     "ConvertTo-Json -InputObject $data -Depth 5 -Compress";

                        string jsonLeases = RunPowerShell(cmdLeases);

                        if (string.IsNullOrWhiteSpace(jsonLeases) || jsonLeases.Trim() == "[]")
                        {
                            WriteLog($"    -> Scope {scopeId} trống hoặc không tồn tại.");
                            continue;
                        }

                        List<tblDHCPClient> scopeClients = JsonConvert.DeserializeObject<List<tblDHCPClient>>(jsonLeases);
                        if (scopeClients != null && scopeClients.Count > 0)
                        {
                            allClients.AddRange(scopeClients);
                            WriteLog($"    -> Lấy thành công {scopeClients.Count} thiết bị từ Scope {scopeId}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteLog($"Lỗi khi quét Scope {scopeId}: " + ex.Message);
                    }
                }

                if (allClients.Count == 0)
                {
                    WriteLog("Quét hoàn tất. Không tìm thấy thiết bị nào trên các Scopes đã chỉ định.");
                    return;
                }

                WriteLog("Đang lấy danh sách MAC Filters...");
                string cmdFilters = utf8Config +
                                    "@(Get-DhcpServerv4Filter | Select-Object " +
                                    "@{Name='MACAddress';Expression={$_.MacAddress}}, Description) | ConvertTo-Json";

                string jsonFilters = RunPowerShell(cmdFilters);

                List<MacFilterItem> listFilters = new List<MacFilterItem>();
                if (!string.IsNullOrWhiteSpace(jsonFilters) && jsonFilters.Trim() != "[]")
                {
                    listFilters = JsonConvert.DeserializeObject<List<MacFilterItem>>(jsonFilters);
                }

                var filterMap = listFilters
                    .Where(f => !string.IsNullOrEmpty(f.MACAddress))
                    .GroupBy(f => f.MACAddress)
                    .ToDictionary(g => g.Key, g => g.First().Description, StringComparer.OrdinalIgnoreCase);

                foreach (var client in allClients)
                {
                    if (!string.IsNullOrEmpty(client.MACAddress) && filterMap.TryGetValue(client.MACAddress, out string filterDesc))
                    {
                        if (!string.IsNullOrWhiteSpace(filterDesc))
                        {
                            client.Description = filterDesc;
                        }
                    }
                }

                WriteLog($"Tổng hợp thành công {allClients.Count} thiết bị từ các Scopes. Tiến hành lưu vào Database...");
                SaveToDatabase(allClients);
            }
            catch (Exception ex)
            {
                WriteLog("Lỗi nghiêm trọng trong quá trình SyncDHCP: " + ex.ToString());
            }
        }

        private void SaveToDatabase(List<tblDHCPClient> lstClient)
        {
            try
            {
                using (DHCPdbDataContext db = new DHCPdbDataContext())
                {
                    // 1. Chuẩn hóa và lọc trùng các bản ghi nhận về từ DHCP server
                    var validClients = lstClient
                        .Where(x => !string.IsNullOrEmpty(x.MACAddress) && !string.IsNullOrEmpty(x.IPAddress))
                        .GroupBy(x => new { MAC = x.MACAddress.Trim().ToUpper(), IP = x.IPAddress.Trim() })
                        .Select(g => g.First())
                        .ToList();

                    // 2. Lấy dữ liệu hiện tại trong DB làm bộ nhớ đệm
                    var dbClients = db.tblDHCPClients
                                      .Where(x => x.MACAddress != null && x.IPAddress != null)
                                      .AsEnumerable()
                                      .ToDictionary(
                                          x => $"{x.MACAddress.Trim().ToUpper()}|{x.IPAddress.Trim()}",
                                          x => x,
                                          StringComparer.OrdinalIgnoreCase
                                      );

                    int updateCount = 0;
                    int insertCount = 0;

                    foreach (var item in validClients)
                    {
                        string searchKey = $"{item.MACAddress.Trim().ToUpper()}|{item.IPAddress.Trim()}";

                        if (dbClients.TryGetValue(searchKey, out tblDHCPClient client))
                        {
                            // Cập nhật thông tin thiết bị đang hoạt động
                            client.HostName = item.HostName;
                            client.Description = item.Description;
                            client.LeaseExpiryTime = item.LeaseExpiryTime;
                            client.LastUpdate = DateTime.Now;
                            updateCount++;
                        }
                        else
                        {
                            // Thêm mới thiết bị
                            item.MACAddress = item.MACAddress.Trim().ToUpper();
                            item.LastUpdate = DateTime.Now;
                            db.tblDHCPClients.InsertOnSubmit(item);
                            insertCount++;
                        }
                    }

                    // Thực thi lưu xuống Database các hoạt động Cập nhật/Thêm mới trước
                    db.SubmitChanges();
                    WriteLog($"Đồng bộ thành công! (Thêm mới: {insertCount}, Cập nhật: {updateCount})");

                    // 3. TIẾN TRÌNH DỌN DẸP TỰ ĐỘNG (ĐÃ THAY ĐỔI THEO YÊU CẦU)
                    // Hễ cứ LeaseExpiryTime nhỏ hơn thời gian hiện tại là xóa ngay, không so sánh Description
                    DateTime thresholdTime = DateTime.Now;

                    var expiredClients = db.tblDHCPClients
                        .Where(x => x.LeaseExpiryTime < thresholdTime)
                        .ToList();

                    if (expiredClients.Any())
                    {
                        int deleteCount = expiredClients.Count;
                        db.tblDHCPClients.DeleteAllOnSubmit(expiredClients);
                        db.SubmitChanges(); // Thực thi xóa khỏi Database
                        WriteLog($"[Cleanup] Đã tự động dọn dẹp sạch sẽ {deleteCount} địa chỉ MAC/IP đã hết hạn ra khỏi DB.");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog("Lỗi nghiêm trọng trong quá trình lưu và dọn dẹp Database: " + ex.ToString());
            }
        }

        private readonly object _logLock = new object();

        private void WriteLog(string msg)
        {
            lock (_logLock)
            {
                try
                {
                    string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    string logPath = Path.Combine(exeDirectory, "DHCPService_Log.txt");
                    string newLogLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}";

                    List<string> logLines = new List<string>();

                    if (File.Exists(logPath))
                    {
                        logLines = File.ReadAllLines(logPath, Encoding.UTF8).ToList();
                    }

                    logLines.Add(newLogLine);

                    if (logLines.Count > 1000)
                    {
                        logLines = logLines.Skip(logLines.Count - 1000).ToList();
                    }

                    File.WriteAllLines(logPath, logLines, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    try
                    {
                        EventLog.WriteEntry("DHCPSyncService", "Không thể ghi log file: " + ex.Message + ". Log gốc: " + msg, EventLogEntryType.Warning);
                    }
                    catch { }
                }
            }
        }
    }

    public class MacFilterItem
    {
        public string MACAddress { get; set; }
        public string Description { get; set; }
    }
}