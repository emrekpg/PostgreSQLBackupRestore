using Microsoft.Win32;
using Npgsql;
using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PostgreSQLBackupRestore
{
    public static class ProcessExtensions
    {
        public static Task WaitForExitAsync(this Process process)
        {
            var tcs = new TaskCompletionSource<bool>();

            void ProcessExited(object sender, EventArgs args)
            {
                tcs.TrySetResult(true);
            }

            process.EnableRaisingEvents = true;
            process.Exited += ProcessExited;

            return tcs.Task.ContinueWith(t =>
            {
                process.Exited -= ProcessExited;
                return t;
            }, TaskScheduler.Default).Unwrap();
        }
    }

    public partial class MainWindow : Window
    {
        private NpgsqlConnection connection;
        private ILogger logger;

        public MainWindow()
        {
            InitializeComponent();
            logger = new FileLogger(AppDomain.CurrentDomain.BaseDirectory, txtLog);
        }

        private string BuildConnectionString(string ipAddress, string dbName = "")
        {
            return $"Host={ipAddress};Database={dbName};Username=postgres;Password=123456;Timeout=60;Pooling=true;";
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            progressBarBackup.Visibility = Visibility.Visible;
            progressBarBackup.IsIndeterminate = true;

            try
            {
                string connectionString = BuildConnectionString(txtIpAddress.Text);
                await ExecuteDatabaseCommand(connectionString, "SELECT datname as database_name FROM pg_database", cbDatabases);
                lbSchemas.ItemsSource = null; // Şema listesini temizle
                await logger.LogAsync("Bağlantı başarıyla gerçekleştirildi.");
            }
            catch (Exception ex)
            {
                await logger.LogAsync($"Bağlantı hatası: {ex.Message}");
            }
            finally
            {
                // ProgressBar'ı gizle
                progressBarBackup.Visibility = Visibility.Collapsed;
                progressBarBackup.IsIndeterminate = false;
            }
        }


        private async void cbDatabases_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbDatabases.SelectedItem != null)
            {
                DataRowView selectedDatabase = (DataRowView)cbDatabases.SelectedItem;
                string databaseName = selectedDatabase["database_name"].ToString();
                string connectionString = BuildConnectionString(txtIpAddress.Text, databaseName);

                await ExecuteDatabaseCommand(connectionString, $"SELECT schema_name FROM information_schema.schemata WHERE catalog_name = '{databaseName}'", lbSchemas);
            }
        }

        private async Task FillDataTableAsync(NpgsqlConnection connection, string query, DataTable dataTable)
        {
            using (NpgsqlCommand command = new NpgsqlCommand(query, connection))
            {
                using (NpgsqlDataReader reader = (NpgsqlDataReader)await command.ExecuteReaderAsync())
                {
                    dataTable.Load(reader);
                }
            }
        }

        private async Task ExecuteDatabaseCommand(string connectionString, string query, dynamic control)
        {
            try
            {
                using (NpgsqlConnection dbConnection = new NpgsqlConnection(connectionString))
                {
                    await dbConnection.OpenAsync();
                    DataTable dt = new DataTable();
                    await FillDataTableAsync(dbConnection, query, dt);
                    control.ItemsSource = dt.DefaultView;
                }
                await logger.LogAsync($"Veritabanı sorgusu başarıyla çalıştırıldı: {query}");
            }
            catch (Exception ex)
            {
                await logger.LogAsync($"Veritabanı sorgusu çalıştırılırken bir hata oluştu: {ex.Message}");
            }
        }

        private async void lbSchemas_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            progressBarBackup.Visibility = Visibility.Visible;
            progressBarBackup.IsIndeterminate = true;

            if (lbSchemas.SelectedItem != null)
            {
                DataRowView selectedSchema = (DataRowView)lbSchemas.SelectedItem;
                string schemaName = selectedSchema["schema_name"].ToString();
                string databaseName = ((DataRowView)cbDatabases.SelectedItem)["database_name"].ToString();

                SaveFileDialog saveFileDialog = new SaveFileDialog
                {
                    Filter = "SQL files (*.sql)|*.sql",
                    FileName = $"{databaseName}_{schemaName}_backup.sql"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    Environment.SetEnvironmentVariable("PGPASSWORD", "123456");
                    string pgDumpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pg_dump.exe");
                    string backupCommand = $"\"{pgDumpPath}\" --host \"{txtIpAddress.Text}\" --port \"5432\" --username \"postgres\" --no-password --verbose --format=c --blobs --schema \"\\\"{schemaName}\\\"\" -f \"{saveFileDialog.FileName}\" \"{databaseName}\"";

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process process = new Process { StartInfo = psi })
                    {
                        process.Start();

                        StreamWriter sw = process.StandardInput;
                        StreamReader sr = process.StandardError;

                        if (sw.BaseStream.CanWrite)
                        {
                            sw.WriteLine(backupCommand);
                            sw.WriteLine("exit");
                        }

                        string errors = await sr.ReadToEndAsync();

                        await process.WaitForExitAsync();

                        if (string.IsNullOrEmpty(errors))
                        {
                            await logger.LogAsync ("Yedekleme tamamlandı.");
                        }
                        else
                        {
                            await logger.LogAsync($"Hata oluştu: {errors}");
                        }
                        progressBarBackup.Visibility = Visibility.Collapsed;
                        progressBarBackup.IsIndeterminate = false;
                    }
                }
            }
        }

        private string backupFilePath;

        private void SelectBackupFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "SQL files (*.sql)|*.sql"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                backupFilePath = openFileDialog.FileName;
                txtBackupFilePath.Text = backupFilePath;
            }
        }

        private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            progressBarRestore.Visibility = Visibility.Visible;
            progressBarRestore.IsIndeterminate = true;

            try
            {
                if (!string.IsNullOrEmpty(backupFilePath))
                {
                    DataRowView selectedDatabase = (DataRowView)cbDatabasesRestore.SelectedItem;

                    if (selectedDatabase != null)
                    {
                        string databaseName = selectedDatabase["database_name"].ToString();

                        Environment.SetEnvironmentVariable("PGPASSWORD", "123456");

                        string pgRestorePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pg_restore.exe");
                        string restoreCommand = $"\"{pgRestorePath}\" -h \"{txtIpAddressRestore.Text}\" -U postgres -d \"{databaseName}\" -1 \"{backupFilePath}\"";

                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            RedirectStandardInput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using (Process process = new Process { StartInfo = psi })
                        {
                            process.Start();

                            using (StreamWriter sw = process.StandardInput)
                            {
                                if (sw.BaseStream.CanWrite)
                                {
                                    await sw.WriteLineAsync(restoreCommand);
                                    await sw.WriteLineAsync("exit");
                                }
                            }

                            await process.WaitForExitAsync();
                        }

                        await logger.LogAsync("Yedek geri yükleme tamamlandı.");
                    }
                    else
                    {
                        MessageBox.Show("Lütfen bir veritabanı seçin.");
                    }
                }
                else
                {
                    MessageBox.Show("Lütfen bir yedek dosyası seçin.");
                }
            }
            catch (Exception ex)
            {
                await logger.LogAsync($"Hata Oluştu: {ex.ToString()}");
            }
            finally
            {
                progressBarRestore.Visibility = Visibility.Collapsed;
                progressBarRestore.IsIndeterminate = false;
            }
        }


        private async void ConnectRestore_Click(object sender, RoutedEventArgs e)
        {
            progressBarRestore.Visibility = Visibility.Visible;
            progressBarRestore.IsIndeterminate = true;

            try
            {
                string connectionString = BuildConnectionString(txtIpAddressRestore.Text);
                await ExecuteDatabaseCommand(connectionString, "SELECT datname as database_name FROM pg_database", cbDatabasesRestore);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Bağlantıda bir hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                await logger.LogAsync($"Bağlantı hatası: {ex.Message}");
            }
            finally
            {
                progressBarRestore.Visibility = Visibility.Collapsed;
                progressBarRestore.IsIndeterminate = false;
            }
        }


        private async void DropCascade_Click(object sender, RoutedEventArgs e)
        {
            if (lbSchemasRestore.SelectedItem != null)
            {
                DataRowView selectedSchema = (DataRowView)lbSchemasRestore.SelectedItem;
                string schemaName = selectedSchema["schema_name"].ToString();
                string databaseName = ((DataRowView)cbDatabasesRestore.SelectedItem)["database_name"].ToString();
                string connectionString = BuildConnectionString(txtIpAddressRestore.Text, databaseName);

                progressBarRestore.Visibility = Visibility.Visible;
                progressBarRestore.IsIndeterminate = true;

                using (NpgsqlConnection dbConnection = new NpgsqlConnection(connectionString))
                {
                    try
                    {
                        await dbConnection.OpenAsync();

                        // Şemanın var olup olmadığını kontrol et
                        NpgsqlCommand checkSchemaCommand = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM information_schema.schemata WHERE schema_name = @schemaName)", dbConnection);
                        checkSchemaCommand.Parameters.AddWithValue("schemaName", schemaName);
                        bool schemaExists = (bool)await checkSchemaCommand.ExecuteScalarAsync();

                        if (schemaExists)
                        {
                            // Şemayı sil
                            NpgsqlCommand dropSchemaCommand = new NpgsqlCommand($"DROP SCHEMA \"{schemaName}\" CASCADE", dbConnection);
                            dropSchemaCommand.Parameters.AddWithValue("schemaName", schemaName);
                            await dropSchemaCommand.ExecuteNonQueryAsync();
                            await logger.LogAsync($"Şema başarıyla silindi: {schemaName}");
                        }
                        else
                        {
                            await logger.LogAsync($"Şema bulunamadı: {schemaName}");
                        }
                    }
                    catch (NpgsqlException ex)
                    {
                        await logger.LogAsync($"Hata oluştu: {ex.Message}");
                    }
                    finally
                    {
                        progressBarRestore.Visibility = Visibility.Collapsed;
                        progressBarRestore.IsIndeterminate = false;

                        // Şema listesini yenile.
                        if (dbConnection.State == ConnectionState.Open)
                        {
                            await ExecuteDatabaseCommand(connectionString, $"SELECT schema_name FROM information_schema.schemata WHERE catalog_name = '{databaseName}'", lbSchemasRestore);
                        }
                    }
                }
            }
            else
            {
                MessageBox.Show("Lütfen silmek istediğiniz şemayı seçin.");
            }
        }

        private async void cbDatabasesRestore_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbDatabasesRestore.SelectedItem != null)
            {
                DataRowView selectedDatabase = (DataRowView)cbDatabasesRestore.SelectedItem;
                string databaseName = selectedDatabase["database_name"].ToString();
                string connectionString = BuildConnectionString(txtIpAddressRestore.Text, databaseName);

                await ExecuteDatabaseCommand(connectionString, $"SELECT schema_name FROM information_schema.schemata WHERE catalog_name = '{databaseName}'", lbSchemasRestore);
            }
        }

        private async void RefreshSchemasRestore_Click(object sender, RoutedEventArgs e)
        {
            if (cbDatabasesRestore.SelectedItem != null)
            {
                DataRowView selectedDatabase = (DataRowView)cbDatabasesRestore.SelectedItem;
                string databaseName = selectedDatabase["database_name"].ToString();
                string connectionString = BuildConnectionString(txtIpAddressRestore.Text, databaseName);

                await ExecuteDatabaseCommand(connectionString, $"SELECT schema_name FROM information_schema.schemata WHERE catalog_name = '{databaseName}'", lbSchemasRestore);
            }
        }

        private async void RefreshSchemasBackup_Click(object sender, RoutedEventArgs e)
        {
            if (cbDatabases.SelectedItem != null)
            {
                DataRowView selectedDatabase = (DataRowView)cbDatabases.SelectedItem;
                string databaseName = selectedDatabase["database_name"].ToString();
                string connectionString = BuildConnectionString(txtIpAddress.Text, databaseName);

                await ExecuteDatabaseCommand(connectionString, $"SELECT schema_name FROM information_schema.schemata WHERE catalog_name = '{databaseName}'", lbSchemas);
            }
            else
            {
                MessageBox.Show("Lütfen bir veritabanı seçin.");
            }
        }

        public interface ILogger
        {
            Task LogAsync(string message);
        }

        public class FileLogger : ILogger
        {
            private readonly string logFilePath;
            private TextBox logTextBox;

            public FileLogger(string logDirectory, TextBox textBox)
            {
                Directory.CreateDirectory(logDirectory);
                // Log dosyasının adını "log.txt" olarak sabit tutuyoruz.
                logFilePath = Path.Combine(logDirectory, "log.txt");
                logTextBox = textBox;
            }

            public async Task LogAsync(string message)
            {
                string logFileName = "ApplicationLog.txt"; // Log dosyasının adı
                string logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logFileName); // Log dosyasının tam yolu

                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";

                try
                {
                    using (StreamWriter writer = new StreamWriter(logFilePath, true))
                    {
                        await writer.WriteLineAsync(logMessage);
                    }

                    if (logTextBox.Dispatcher.CheckAccess())
                    {
                        logTextBox.AppendText(logMessage + Environment.NewLine);
                        logTextBox.ScrollToEnd(); // TextBox'ı aşağı kaydır
                    }
                    else
                    {
                        logTextBox.Dispatcher.Invoke(() =>
                        {
                            logTextBox.AppendText(logMessage + Environment.NewLine);
                            logTextBox.ScrollToEnd(); // TextBox'ı aşağı kaydır
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Logging error: {ex.Message}");
                }
            }
        }
    }
}
