﻿using Macro.Extensions;
using Macro.Infrastructure;
using Macro.Infrastructure.Manager;
using Macro.Infrastructure.Serialize;
using Macro.Models;
using Macro.View;
using MahApps.Metro.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Utils;
using Utils.Extensions;
using Utils.Infrastructure;
using EventType = Macro.Models.EventType;
using InputManager = Macro.Infrastructure.Manager.InputManager;
using Message = Utils.Document.Message;
using Point = System.Windows.Point;
using Rect = Utils.Infrastructure.Rect;
using Version = Macro.Models.Version;

namespace Macro
{
    public partial class MainWindow : MetroWindow
    {
        private TaskQueue _taskQueue;
        private string _path;
        private KeyValuePair<string, Process>[] _processes;
        private IConfig _config;
        private Bitmap _bitmap;
        private List<CaptureView> _captureViews;
        public MainWindow()
        {
            _taskQueue = new TaskQueue();
            _captureViews = new List<CaptureView>();

            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }
        
        private void Init()
        {
            //window7 not support
            if (Environment.OSVersion.Version.Major >= 6 && Environment.OSVersion.Version.Minor > 1)
            {
                NativeHelper.SetProcessDpiAwareness(PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE);
            }
            else
            {
                this.MessageShow("Error", DocumentHelper.Get(Message.FailedOSVersion));
            }
            foreach(var item in DisplayHelper.MonitorInfo())
            {
                _captureViews.Add(new CaptureView(item));
            }
            _config = ObjectExtensions.GetInstance<IConfig>();

            Refresh();
            ProcessManager.AddJob(OnProcessCallback);
            _taskQueue.Enqueue(SaveLoad, _path);
        }

        private void Refresh()
        {
            _path = _config.SavePath;
            if (string.IsNullOrEmpty(_path))
                _path = ConstHelper.DefaultSavePath;
            else
                _path += @"\";
            if (!Directory.Exists(_path))
                Directory.CreateDirectory(_path);
            _path = $"{_path}{ConstHelper.DefaultSaveFile}";

            _processes = Process.GetProcesses().Where(r=>r.MainWindowHandle != IntPtr.Zero)
                                                .Select(r => new KeyValuePair<string, Process>(r.ProcessName, r))
                                                .OrderBy(r=>r.Key).ToArray();
            comboProcess.ItemsSource = _processes;
            comboProcess.DisplayMemberPath = "Key";
            comboProcess.SelectedValuePath = "Value";

            var labels = ObjectExtensions.FindChildren<Label>(this);
            foreach (var label in labels)
            {
                BindingOperations.GetBindingExpressionBase(label, ContentProperty).UpdateTarget();
            }
            var buttons = ObjectExtensions.FindChildren<Button>(this);
            foreach (var button in buttons)
            {
                if (button.Equals(btnSetting) || button.Content == null || !(button.Content is string))
                    continue;

                BindingOperations.GetBindingExpressionBase(button, ContentProperty).UpdateTarget();
            }
            BindingOperations.GetBindingExpressionBase(this, TitleProperty).UpdateTarget();
            configView.Clear();
        }

        private bool TryModelValidate(EventTriggerModel model, out Message message)
        {
            message = Message.Success;
            model.KeyboardCmd = model.KeyboardCmd.Replace(" ", "");
            if (model.Image == null)
            {
                message = Message.FailedImageValidate;
                return false;
            }
            if (model.EventType == EventType.Mouse && model.MouseTriggerInfo.MouseInfoEventType == MouseEventType.None)
            {
                message = Message.FailedMouseCoordinatesValidate;
                return false;
            }

            if (string.IsNullOrEmpty(model.KeyboardCmd) && model.EventType == EventType.Keyboard)
            {
                message = Message.FailedKeyboardCommandValidate;
                return false;
            }
            if (string.IsNullOrEmpty(model.ProcessInfo.ProcessName))
            {
                message = Message.FailedProcessValidate;
                return false;
            }
            return true;
        }
        private void Capture()
        {
            Clear();
            WindowState = WindowState.Minimized;

            foreach(var item in _captureViews)
            {
                item.ShowActivate();
            }
        }
        private void Clear()
        {
            btnDelete.Visibility = Visibility.Collapsed;
            _bitmap = null;
            captureImage.Background = System.Windows.Media.Brushes.White;
            configView.Clear();
        }
        private Task Delete()
        {
            configView.CurrentRemove();
            if (File.Exists(_path))
            {
                File.Delete(_path);
                using (var fs = new FileStream(_path, FileMode.CreateNew))
                {
                    foreach (var data in this.configView.DataContext<Models.ViewModel.ConfigEventViewModel>().TriggerSaves)
                    {
                        var bytes = ObjectSerializer.SerializeObject(data);
                        fs.Write(bytes, 0, bytes.Count());
                    }
                    fs.Close();
                }
            }
            return Task.CompletedTask;
        }
        private Task Save()
        {
            if (File.Exists(_path))
                File.Delete(_path);
            using (var fs = new FileStream(_path, FileMode.OpenOrCreate))
            {
                var saves = (configView.DataContext as Models.ViewModel.ConfigEventViewModel).TriggerSaves;
                foreach (var data in saves)
                {
                    var bytes = ObjectSerializer.SerializeObject(data);
                    fs.Write(bytes, 0, bytes.Count());
                }
                fs.Close();
            }
            return Task.CompletedTask;
        }
        private Task SaveLoad(object state)
        {
            var task = new TaskCompletionSource<Task>();
            Dispatcher.Invoke(() => 
            {
                try
                {
                    var models = ObjectSerializer.DeserializeObject<EventTriggerModel>(File.ReadAllBytes(_path));
                    configView.BindingItems(models);
                    task.SetResult(Task.CompletedTask);
                }
                catch (Exception ex)
                {
                    File.Delete(_path);
                    LogHelper.Warning(ex.Message);
                    Task.FromException(new FileLoadException(DocumentHelper.Get(Message.FailedLoadSaveFile)));
                }
            }, DispatcherPriority.Send);
            return task.Task;
        }
        private void VersionCheck()
        {
            if (!_config.VersionCheck)
                return;
            Version version = null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(ConstHelper.VersionUrl);
                using (var response = request.GetResponse())
                {
                    using (var stream = new StreamReader(response.GetResponseStream()))
                    {
                        version = JsonHelper.DeserializeObject<Version>(stream.ReadToEnd());
                    }
                }
            }
            catch(Exception ex)
            {
                LogHelper.Warning(ex.Message);
            }
            
            if(version != null)
            {
                if(version.CompareTo(Version.CurrentVersion) > 0)
                {
                    if(this.MessageShow("Infomation", DocumentHelper.Get(Message.NewVersion), MahApps.Metro.Controls.Dialogs.MessageDialogStyle.AffirmativeAndNegative) == MahApps.Metro.Controls.Dialogs.MessageDialogResult.Affirmative)
                    {
                        Process.Start(ConstHelper.ReleaseUrl);
                    }
                }
            }
        }
        private void ImageTriggerProcess(Process process, Point location, EventTriggerModel model)
        {
            var currentPosition = new Rect();
            NativeHelper.GetWindowRect(process.MainWindowHandle, ref currentPosition);

            var targetFactorX = 1.0F;
            var targetFactorY = 1.0F;

            foreach (var monitor in DisplayHelper.MonitorInfo())
            {
                if (monitor.Rect.IsContain(currentPosition))
                {
                    targetFactorX = monitor.Dpi.X / (model.MonitorInfo.Dpi.X * targetFactorX);
                    targetFactorY = monitor.Dpi.Y / (model.MonitorInfo.Dpi.Y * targetFactorY);
                    break;
                }
            }
            LogHelper.Debug($"Image Location X : {location.X} Location Y : {location.Y} Target X : {location.X * targetFactorX} Target Y : {location.Y * targetFactorY}");

            NativeHelper.PostMessage(process.MainWindowHandle, WindowMessage.LButtonDown, 1, location.ToLParam());
            Task.Delay(100).Wait();
            NativeHelper.PostMessage(process.MainWindowHandle, WindowMessage.LButtonUp, 0, location.ToLParam());
        }
        private void MouseTriggerProcess(Process process, EventTriggerModel model)
        {
            var currentPosition = new Rect();
            NativeHelper.GetWindowRect(process.MainWindowHandle, ref currentPosition);

            var targetFactorX = 1.0F;
            var targetFactorY = 1.0F;

            foreach (var monitor in DisplayHelper.MonitorInfo())
            {
                if (monitor.Rect.IsContain(currentPosition))
                {
                    targetFactorX = monitor.Dpi.X / (model.MonitorInfo.Dpi.X * targetFactorX);
                    targetFactorY = monitor.Dpi.Y / (model.MonitorInfo.Dpi.Y * targetFactorY);
                    break;
                }
            }
            var mousePosition = new Point()
            {
                X = Math.Abs(model.ProcessInfo.Position.Left + model.MouseTriggerInfo.StartPoint.X * -1) * targetFactorX,
                Y = Math.Abs(model.ProcessInfo.Position.Top + model.MouseTriggerInfo.StartPoint.Y * -1) * targetFactorY
            };

            LogHelper.Debug($"Save Position X : {model.MouseTriggerInfo.StartPoint.X} Save Position Y : {model.MouseTriggerInfo.StartPoint.Y} Target X : { mousePosition.X } Target Y : { mousePosition.Y }");
            if(model.MouseTriggerInfo.MouseInfoEventType == MouseEventType.LeftClick)
            {
                NativeHelper.PostMessage(process.MainWindowHandle, WindowMessage.LButtonDown, 1, mousePosition.ToLParam());
                Task.Delay(100).Wait();
                NativeHelper.PostMessage(process.MainWindowHandle, WindowMessage.LButtonUp, 0, mousePosition.ToLParam());
            }
            else if (model.MouseTriggerInfo.MouseInfoEventType == MouseEventType.RightClick)
            {
                NativeHelper.PostMessage(process.MainWindowHandle, WindowMessage.RButtonDown, 1, mousePosition.ToLParam());
                Task.Delay(100).Wait();
                NativeHelper.PostMessage(process.MainWindowHandle, WindowMessage.RButtonDown, 0, mousePosition.ToLParam());
            }
            else if (model.MouseTriggerInfo.MouseInfoEventType == MouseEventType.DragAndDrop)
            {
                var mouseEndPosition = new Point()
                {
                    X = Math.Abs(model.ProcessInfo.Position.Left + model.MouseTriggerInfo.EndPoint.X * -1) * targetFactorX,
                    Y = Math.Abs(model.ProcessInfo.Position.Top + model.MouseTriggerInfo.EndPoint.Y * -1) * targetFactorY
                };
                NativeHelper.PostMessage(process.MainWindowHandle, WindowMessage.LButtonDown, 1, mousePosition.ToLParam());
                Task.Delay(100).Wait();
                NativeHelper.PostMessage(process.MainWindowHandle, WindowMessage.LButtonDown, 1, mouseEndPosition.ToLParam());
                Task.Delay(100).Wait();
                NativeHelper.PostMessage(process.MainWindowHandle, WindowMessage.LButtonUp, 0, mousePosition.ToLParam());
            }
        }
        private void KeyboardTriggerProcess(Process process, EventTriggerModel model)
        {
            var hWndActive = NativeHelper.GetForegroundWindow();
            Task.Delay(100).Wait();
            NativeHelper.SetForegroundWindow(process.MainWindowHandle);
            var commands = model.KeyboardCmd.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            var modifiedKey = commands.Where(r =>
            {
                var keyCode = (KeyCode)Enum.Parse(typeof(KeyCode), $"{r}", true);
                return keyCode.IsExtendedKey();
            }).Select(r =>
            {
                var keyCode = (KeyCode)Enum.Parse(typeof(KeyCode), $"{r}", true);
                return keyCode;
            });

            var keys = commands.Where(r =>
            {
                var keyCode = (KeyCode)Enum.Parse(typeof(KeyCode), r, true);
                return !keyCode.IsExtendedKey();
            }).Select(r =>
            {
                var keyCode = (KeyCode)Enum.Parse(typeof(KeyCode), r, true);
                return keyCode;
            });
            ObjectExtensions.GetInstance<InputManager>().Keyboard.ModifiedKeyStroke(modifiedKey, keys);
            NativeHelper.SetForegroundWindow(hWndActive);
        }
        private bool TriggerProcess(EventTriggerModel model, CancellationToken token)
        {
            KeyValuePair<string, Process>[] processes = null;
            Dispatcher.Invoke(() =>
            {
                processes = _processes.Where(r => r.Key.Equals(model.ProcessInfo.ProcessName)).ToArray();
            });
            for (int i = 0; i < processes.Length; ++i)
            {
                if (DisplayHelper.ProcessCapture(processes.ElementAt(i).Value, out Bitmap bmp))
                {
                    var factor = NativeHelper.GetSystemDpi();
                    var sourceBmp = bmp.Resize((int)Math.Truncate(bmp.Width * (factor.X / ConstHelper.DefaultDPI)), (int)Math.Truncate(bmp.Height * (factor.Y / ConstHelper.DefaultDPI)));
                    var souceFactorX = factor.X / (model.MonitorInfo.Dpi.X * 1.0F);
                    var souceFactorY = factor.Y / (model.MonitorInfo.Dpi.Y * 1.0F);

                    var targetBmp = model.Image.Resize((int)Math.Truncate(model.Image.Width * souceFactorX), (int)Math.Truncate(model.Image.Height * souceFactorY));

                    Dispatcher.Invoke(() =>
                    {
                        captureImage.Background = new ImageBrush(sourceBmp.ToBitmapSource());
                    });
                    var similarity = OpenCVHelper.Search(sourceBmp, targetBmp, out Point location);
                    LogHelper.Debug($"Similarity : {similarity} % max Loc : X : {location.X} Y: {location.Y}");

                    if (similarity >= _config.Similarity)
                    {
                        if(model.SubEventTriggers.Count > 0)
                        {
                            for(int ii=0; ii<model.SubEventTriggers.Count; ++ii)
                            {
                                if (!TriggerProcess(model.SubEventTriggers[ii], token))
                                    break;
                            }
                        }
                        else
                        {
                            if (model.EventType == EventType.Mouse)
                            {
                                MouseTriggerProcess(processes.ElementAt(i).Value, model);
                            }
                            else if (model.EventType == EventType.Image)
                            {
                                location.X = location.X / souceFactorX + (model.Image.Width / 2);
                                location.Y = location.Y / souceFactorY + (model.Image.Height / 2);
                                ImageTriggerProcess(processes.ElementAt(i).Value, location, model);
                            }
                            else if (model.EventType == EventType.Keyboard)
                            {
                                KeyboardTriggerProcess(processes.ElementAt(i).Value, model);
                            }
                        }
                        if (!TokenCheckDelay(model.AfterDelay, token))
                            break;
                    }
                }
            }
            return TokenCheckDelay(_config.ItemDelay, token);
        }
        private bool TokenCheckDelay(int millisecondsDelay, CancellationToken token)
        {
            try
            {
                Task.Delay(millisecondsDelay, token).Wait();
            }
            catch (AggregateException ex)
            {
                LogHelper.Debug(ex.Message);
            }
            return !token.IsCancellationRequested;
        }
        private Task OnProcessCallback(CancellationToken token)
        {
            var tcs = new TaskCompletionSource<Task>();
            List<EventTriggerModel> saves = null;
            
            Dispatcher.Invoke(() => 
            {
                saves = configView.TriggerSaves;
            });
            if(saves != null)
            {
                foreach (var save in saves) 
                {
                    if (token.IsCancellationRequested)
                        break;
                    if (!TriggerProcess(save, token))
                        break;
                }
                TokenCheckDelay(_config.Period, token);
                tcs.SetResult(Task.CompletedTask);
            }
            else
            {
                tcs.TrySetCanceled();
            }
            return tcs.Task;
        }
    }
}
