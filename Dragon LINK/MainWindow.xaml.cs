using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.IO.Ports;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Collections.ObjectModel;
using System.Text;

namespace Dragon_LINK
{
    [SupportedOSPlatform("windows10.0.17763.0")]
    public sealed partial class MainWindow : Window
    {
        private readonly DispatcherTimer portScanTimer;
        private ConnectionState connectionState = ConnectionState.Disconnected;
        private const string APP = "DRAGON";
        private CancellationTokenSource? cancellationTokenSource;
        private readonly SemaphoreSlim connectionLock = new SemaphoreSlim(1, 1);

        public enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            Disconnecting
        }

        private class LinkPortInfo
        {
            public string? PortName { get; set; }
            public string? UID { get; set; }
            public string? Version { get; set; }
            public string? Modele { get; set; }
            public override string ToString() => $"{PortName} | {UID}";
        }

        private DispatcherTimer pingTimer = new();
        private DateTime lastCommandSent = DateTime.MinValue;
        private DateTime lastResponseReceived = DateTime.MinValue;
        private SerialPort? activeSerialPort = null;
        private readonly object syncLock = new();
        private readonly ObservableCollection<LinkPortInfo> availablePorts = new();
        private readonly TimeSpan pingTimeout = TimeSpan.FromSeconds(5);
        private readonly TimeSpan pingInterval = TimeSpan.FromSeconds(4);

        public MainWindow()
        {
            InitializeComponent();
            ComboLinkPorts.ItemsSource = availablePorts;

            portScanTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            portScanTimer.Tick += PortScanTimer_Tick;
            portScanTimer.Start();
        }

        private void PortScanTimer_Tick(object? sender, object e)
        {
            if (connectionState == ConnectionState.Disconnected)
            {
                _ = Task.Run(() => getAvailableLinkPorts());
            }
        }

        private async void ClickLinkConnect(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TextLinkPassword.Password) || ComboLinkPorts.SelectedItem == null)
            {
                await ShowError("Merci de choisir un port LINK et de spécifier un mot de passe !");
                return;
            }

            await connectionLock.WaitAsync();
            try
            {
                if (connectionState == ConnectionState.Connected)
                {
                    await DisconnectAsync();
                }
                else
                {
                    await ConnectAsync();
                }
            }
            finally
            {
                connectionLock.Release();
            }
        }

        private async Task ConnectAsync()
        {
            if (connectionState != ConnectionState.Disconnected) return;

            connectionState = ConnectionState.Connecting;
            UpdateConnectionUI();
            WriteTerminal(text: ">> Tentative de connexion sur " + ComboLinkPorts.SelectedItem);

            if (ComboLinkPorts.SelectedItem is LinkPortInfo info)
            {
                textLinkUID.Text = info.UID;
                textLinkVersion.Text = info.Version;
                textLinkModele.Text = info.Modele;

                string portName = info.PortName ?? string.Empty;
                string password = TextLinkPassword.Password;
                bool startOk = false;

                try
                {
                    using var serial = CreateSerialPort(portName);
                    serial.Open();
                    serial.DiscardInBuffer();

                    string cmd = $"LINK{APP}:START:{password}\r\n";
                    await serial.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(cmd));

                    string? response = await ReadLineAsync(serial);

                    if (response != null)
                    {
                        // Nettoyer la réponse (supprimer les caractères de contrôle)
                        response = response.Trim('\r', '\n', ' ');

                        if (response.StartsWith($"LINK{APP}:"))
                        {
                            var parts = response.Split(':');

                            if (parts.Length >= 2)
                            {
                                string responseCode = parts[1].Trim();

                                switch (responseCode)
                                {
                                    case "START_OK":
                                        WriteTerminal(">> Connexion acceptée par le périphérique");
                                        startOk = true;
                                        break;
                                    case "START_NOK":
                                        WriteTerminal(">> Connexion refusée: mot de passe incorrect");
                                        break;
                                    case "PREFIX_INVALID":
                                        WriteTerminal(">> Erreur: préfixe invalide");
                                        break;
                                    case "COMMAND_UNKNOWN":
                                        WriteTerminal(">> Erreur: commande inconnue");
                                        break;
                                    case "UNCONNECTED":
                                        WriteTerminal(">> Erreur: périphérique non connecté");
                                        break;
                                    default:
                                        WriteTerminal($">> Réponse non reconnue: '{responseCode}'");
                                        break;
                                }
                            }
                            else
                            {
                                WriteTerminal(">> Erreur: réponse incomplète");
                            }
                        }
                        else
                        {
                            WriteTerminal(">> Erreur: réponse non conforme au protocole");
                        }
                    }
                    else
                    {
                        WriteTerminal(">> Erreur: aucune réponse reçue (timeout)");
                    }
                }
                catch (Exception ex)
                {
                    await ShowError("Erreur de communication avec le port : " + ex.Message);
                    WriteTerminal(text: ">> Échec de la connexion sur " + portName);
                    connectionState = ConnectionState.Disconnected;
                    UpdateConnectionUI();
                    return;
                }

                if (startOk)
                {
                    connectionState = ConnectionState.Connected;
                    UpdateConnectionUI();
                    WriteTerminal(text: ">> Connexion établie sur " + portName);

                    activeSerialPort = CreateSerialPort(portName);
                    activeSerialPort.Open();
                    activeSerialPort.DataReceived += OnDataReceived;

                    lastCommandSent = DateTime.Now;
                    lastResponseReceived = DateTime.Now;

                    pingTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1)
                    };
                    pingTimer.Tick += PingTimer_Tick;
                    pingTimer.Start();
                }
                else
                {
                    await ShowError("Mot de passe incorrect, connexion refusée ou erreur protocole !");
                    WriteTerminal(text: ">> Connexion refusée sur " + portName);
                    connectionState = ConnectionState.Disconnected;
                    UpdateConnectionUI();
                }
            }
        }

        private async Task DisconnectAsync()
        {
            if (connectionState != ConnectionState.Connected) return;

            connectionState = ConnectionState.Disconnecting;
            UpdateConnectionUI();

            if (ComboLinkPorts.SelectedItem is LinkPortInfo info)
            {
                string portName = info.PortName ?? string.Empty;
                bool stopOk = false;

                try
                {
                    using var serial = CreateSerialPort(portName);
                    serial.Open();
                    serial.DiscardInBuffer();

                    string cmd = $"LINK{APP}:STOP\r\n";
                    await serial.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(cmd));

                    string? response = await ReadLineAsync(serial);

                    if (response != null && response.StartsWith($"LINK{APP}:"))
                    {
                        var parts = response.Split(':');
                        if (parts.Length >= 2)
                        {
                            stopOk = parts[1].Trim() == "STOP_OK";
                        }
                    }
                }
                catch (Exception ex)
                {
                    await ShowError("Erreur de communication lors de la déconnexion : " + ex.Message);
                    WriteTerminal(text: ">> Échec de la déconnexion sur " + portName);
                }

                WriteTerminal(stopOk
                    ? $">> Déconnexion réussie sur {portName}"
                    : $">> Déconnexion refusée ou timeout sur {portName}");
            }

            await CleanupConnectionAsync();
            connectionState = ConnectionState.Disconnected;
            UpdateConnectionUI();
        }

        private async Task CleanupConnectionAsync()
        {
            pingTimer.Stop();

            if (activeSerialPort != null)
            {
                try
                {
                    activeSerialPort.DataReceived -= OnDataReceived;
                    activeSerialPort.Close();
                }
                catch { }
                activeSerialPort = null;
            }

            await Task.Delay(100);
        }

        private void UpdateConnectionUI()
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateConnectionUIInternal();
            }
            else
            {
                DispatcherQueue.TryEnqueue(() => UpdateConnectionUIInternal());
            }
        }

        private void UpdateConnectionUIInternal()
        {
            switch (connectionState)
            {
                case ConnectionState.Disconnected:
                    btnLinkConnect.Content = "Connecter";
                    LinkStatus.Text = "Déconnecté";
                    borderLinkStatus.Background = (Brush)Application.Current.Resources["APP_ErrorAccent"];
                    ComboLinkPorts.IsEnabled = true;
                    TextLinkPassword.IsEnabled = true;
                    sP_LinkControls.Visibility = Visibility.Collapsed;
                    borderLinkInfo.Visibility = Visibility.Collapsed;
                    break;

                case ConnectionState.Connecting:
                    btnLinkConnect.Content = "Annuler";
                    LinkStatus.Text = "Connexion...";
                    borderLinkStatus.Background = (Brush)Application.Current.Resources["APP_WarningAccent"];
                    ComboLinkPorts.IsEnabled = false;
                    TextLinkPassword.IsEnabled = false;
                    borderLinkInfo.Visibility = Visibility.Visible;
                    break;

                case ConnectionState.Connected:
                    btnLinkConnect.Content = "Déconnecter";
                    LinkStatus.Text = "Connecté";
                    borderLinkStatus.Background = (Brush)Application.Current.Resources["APP_SuccessAccent"];
                    sP_LinkControls.Visibility = Visibility.Visible;
                    break;

                case ConnectionState.Disconnecting:
                    btnLinkConnect.Content = "Annuler";
                    LinkStatus.Text = "Déconnexion...";
                    borderLinkStatus.Background = (Brush)Application.Current.Resources["APP_WarningAccent"];
                    break;
            }
        }

        private SerialPort CreateSerialPort(string portName)
        {
            return new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 5000,
                WriteTimeout = 3000
            };
        }

        private async Task<string?> ReadLineAsync(SerialPort serial)
        {
            try
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        return serial.ReadLine();
                    }
                    catch (TimeoutException)
                    {
                        return null;
                    }
                }, cancellationTokenSource?.Token ?? default);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private void Button_Click_1(object sender, RoutedEventArgs e) => sP_LinkControls_Click();
        private void Button_Click_2(object sender, RoutedEventArgs e) => sP_LinkControls_Click();
        private void Button_Click_3(object sender, RoutedEventArgs e) => sP_LinkControls_Click();

        private void sP_LinkControls_Click()
        {
            foreach (Button btn in sP_LinkControls.Children.OfType<Button>())
            {
                btn.IsEnabled = false;
            }
        }

        private void keyLinkConnect(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                ClickLinkConnect(sender, e);
            }
        }

        private async Task ShowError(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "Erreur",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private void WriteTerminal(string text)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                terminalOutput.Text += text + Environment.NewLine;
                terminalOutput.Select(terminalOutput.Text.Length, 0);
            }
            else
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    terminalOutput.Text += text + Environment.NewLine;
                    terminalOutput.Select(terminalOutput.Text.Length, 0);
                });
            }
        }

        private async void getAvailableLinkPorts()
        {
            cancellationTokenSource?.Cancel();
            cancellationTokenSource = new CancellationTokenSource();

            try
            {
                var ports = SerialPort.GetPortNames();
                string? selectedPort = null;
                var tcs = new TaskCompletionSource<string?>();
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        selectedPort = (ComboLinkPorts.SelectedItem as LinkPortInfo)?.PortName;
                        tcs.SetResult(selectedPort);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
                await tcs.Task;

                var foundPorts = new List<LinkPortInfo>();

                foreach (var port in ports)
                {
                    if (cancellationTokenSource.Token.IsCancellationRequested)
                        break;

                    var info = await ProbePortAsync(port, cancellationTokenSource.Token);
                    if (info != null)
                    {
                        foundPorts.Add(info);
                    }
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    availablePorts.Clear();
                    foreach (var port in foundPorts)
                    {
                        availablePorts.Add(port);
                    }

                    if (selectedPort != null)
                    {
                        var selected = availablePorts.FirstOrDefault(p => p.PortName == selectedPort);
                        if (selected != null)
                            ComboLinkPorts.SelectedItem = selected;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Opération annulée, ne rien faire
            }
            catch (Exception ex)
            {
                WriteTerminal($">> Erreur lors de la recherche des ports: {ex.Message}");
            }
        }

        private async Task<LinkPortInfo?> ProbePortAsync(string portName, CancellationToken cancellationToken)
        {
            try
            {
                using var serial = CreateSerialPort(portName);

                try
                {
                    serial.Open();
                }
                catch
                {
                    return null;
                }

                string request = $"LINK{APP}:GETV\r\n";
                serial.DiscardInBuffer();

                try
                {
                    await serial.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(request), cancellationToken);
                }
                catch
                {
                    return null;
                }

                string response;
                try
                {
                    response = await ReadLineAsync(serial) ?? string.Empty;
                }
                catch
                {
                    return null;
                }

                if (response.StartsWith($"LINK{APP}:"))
                {
                    var parts = response.Trim().Split(':');
                    if (parts.Length == 4)
                    {
                        return new LinkPortInfo
                        {
                            PortName = portName,
                            UID = parts[1],
                            Version = parts[2],
                            Modele = parts[3]
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                return null;
            }
            return null;
        }

        private void SendCommand(string command)
        {
            if (activeSerialPort != null && activeSerialPort.IsOpen)
            {
                try
                {
                    activeSerialPort.Write(command + "\r\n");
                    lastCommandSent = DateTime.Now;
                }
                catch (Exception ex)
                {
                    WriteTerminal($">> Erreur d'envoi de commande : {ex.Message}");
                }
            }
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (connectionState != ConnectionState.Connected || activeSerialPort == null || !activeSerialPort.IsOpen)
                return;

            try
            {
                string? response = activeSerialPort.ReadLine()?.Trim('\r', '\n');
                if (!string.IsNullOrEmpty(response))
                {
                    if (response.StartsWith($"LINK{APP}:"))
                    {
                        var parts = response.Split(':');
                        if (parts.Length >= 2)
                        {
                            switch (parts[1])
                            {
                                case "PING":
                                    lock (syncLock)
                                    {
                                        lastResponseReceived = DateTime.Now;
                                    }
                                    break;

                                case "STOP_OK":
                                    WriteTerminal(">> Déconnexion confirmée par le périphérique");
                                    break;

                                case "PREFIX_INVALID":
                                case "COMMAND_UNKNOWN":
                                case "UNCONNECTED":
                                    WriteTerminal($">> Erreur protocole : {parts[1]}");
                                    break;

                                default:
                                    break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteTerminal($">> Erreur de lecture : {ex.Message}");
            }
        }

        private void PingTimer_Tick(object? sender, object e)
        {
            if (connectionState != ConnectionState.Connected) return;

            if ((DateTime.Now - lastCommandSent) >= pingInterval)
            {
                SendCommand($"LINK{APP}:PING");
            }

            double secondsSinceLastResponse;
            lock (syncLock)
            {
                secondsSinceLastResponse = (DateTime.Now - lastResponseReceived).TotalSeconds;
            }

            if (secondsSinceLastResponse >= pingTimeout.TotalSeconds)
            {
                WriteTerminal(">> Timeout : perte de connexion avec le périphérique !");
                _ = Task.Run(async () =>
                {
                    await connectionLock.WaitAsync();
                    try
                    {
                        await DisconnectAsync();
                        connectionState = ConnectionState.Disconnected;
                        UpdateConnectionUI();
                    }
                    finally
                    {
                        connectionLock.Release();
                    }
                });
            }
        }
    }
}