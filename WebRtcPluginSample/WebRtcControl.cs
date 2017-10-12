﻿using System;
using System.Collections.Generic;
using System.Text;

using Windows.ApplicationModel.Core;
using Windows.UI.Core;

using Org.WebRtc;
using WebRtcPluginSample.Signalling;
using System.Linq;
using System.Collections.ObjectModel;
using System.Diagnostics;
using WebRtcPluginSample.Model;

namespace WebRtcPluginSample
{
    public class WebRtcControl
    {
        public event Action<string> OnStatusMessageUpdate;      // ステータス変更を通知するイベント

        private readonly CoreDispatcher _uiDispathcer;


        public WebRtcControl()
        {
            _uiDispathcer = CoreApplication.MainView.CoreWindow.Dispatcher;
        }

        public void Initialize()
        {
            // WebRTCライブラリの初期化
            WebRTC.Initialize(_uiDispathcer);
            Conductor.Instance.ETWStatsEnabled = false;

            Cameras = new List<MediaDevice>();
            Microphones = new List<MediaDevice>();
            AudioPlayoutDevices = new List<MediaDevice>();
            // マシン上で使用できるメディアデバイスをすべて取得する
            foreach(var videoCaptureDevice in Conductor.Instance.Media.GetVideoCaptureDevices())
            {
                Cameras.Add(videoCaptureDevice);
            }
            foreach(var audioCaptureDevice in Conductor.Instance.Media.GetAudioCaptureDevices())
            {
                Microphones.Add(audioCaptureDevice);
            }
            foreach(var audioPlayoutDevice in Conductor.Instance.Media.GetAudioPlayoutDevices())
            {
                AudioPlayoutDevices.Add(audioPlayoutDevice);
            }

            // 各種メディアデバイスはリストの先頭のものを使用する
            // Holoはいいけど、Immersiveの場合は考え直すべきです
            if(SelectedCamera == null && Cameras.Count > 0)
            {
                SelectedCamera = Cameras.First();
            }
            
            if(SelectedMicrophone == null && Microphones.Count > 0)
            {
                SelectedMicrophone = Microphones.First();
            }

            if(SelectedAudioPlayoutDevice == null && AudioPlayoutDevices.Count >0)
            {
                SelectedAudioPlayoutDevice = AudioPlayoutDevices.First();
            }

            // マシンに接続されたメディアデバイスが変更されたときのイベントハンドラ
            Conductor.Instance.Media.OnMediaDevicesChanged += OnMediaDeviceChanged;
            // リモートユーザがシグナリングサーバに接続してきたときのハンドラ
            // 自分の初回ログイン、ポーリング時の新規ユーザ追加時にコールされる
            // TODO 選択方法を工夫したいところ
            Conductor.Instance.Signaller.OnPeerConnected += (peerId, peerName) =>
            {
                // リモートユーザのリストを行進する
                if (Peers == null)
                {
                    Peers = new List<Peer>();
                    Conductor.Instance.Peers = Peers;
                }
                Peers.Add(new Peer { Id = peerId, Name = peerName });
                // 接続してきたリモートユーザをPeer候補とする
                SelectedPeer = Peers.First(x => x.Id == peerId);
            };
            // リモートユーザがシグナリングサーバからログアウトしたときのハンドラ
            Conductor.Instance.Signaller.OnPeerDisconnected += (peerId) =>
            {
                var peerToRemove = Peers?.FirstOrDefault(p => p.Id == peerId);
                if (peerToRemove != null)
                    Peers.Remove(peerToRemove);
            };
            // シグナリングサーバへの接続が完了したときのハンドラ
            Conductor.Instance.Signaller.OnSignedIn += () =>
            {
                IsConnected = true;
                IsMicrophoneEnabled = false;
                IsCameraEnabled = false;
                IsConnecting = false;

                OnStatusMessageUpdate?.Invoke("Signed in");
            };
            // シグナリングサーバへの接続が失敗したときのハンドラ
            Conductor.Instance.Signaller.OnServerConnectionFailure += () =>
            {
                IsConnecting = false;

                OnStatusMessageUpdate?.Invoke("Server Connection Failure");
            };
            // シグナリングサーバからログアウトしたときのハンドラ
            Conductor.Instance.Signaller.OnDisconnected += () =>
             {
                 IsConnected = false;
                 IsMicrophoneEnabled = false;
                 IsCameraEnabled = false;
                 IsDisconnecting = false;
                 Peers?.Clear();

                 OnStatusMessageUpdate?.Invoke("Disconnected");
             };

        }

        #region Event Handlers
        /// <summary>
        /// マシンに接続されたメディアデバイスが変更されたときのハンドラ。
        /// メディアデバイスのホットプラグに対応する。
        /// </summary>
        /// <param name="mediaType">接続/切断されたメディアデバイスのメディアタイプ</param>
        private void OnMediaDeviceChanged(MediaDeviceType mediaType)
        {
            switch (mediaType)
            {
                // カメラ
                case MediaDeviceType.MediaDeviceType_VideoCapture:
                    RefreshVideoCaptureDevice(Conductor.Instance.Media.GetVideoCaptureDevices());
                    break;
                // マイク
                case MediaDeviceType.MediaDeviceType_AudioCapture:
                    RefreshAudioCaptureDevices(Conductor.Instance.Media.GetAudioCaptureDevices());
                    break;
                // スピーカー
                case MediaDeviceType.MediaDeviceType_AudioPlayout:
                    RefreshAudioPlayoutDevices(Conductor.Instance.Media.GetAudioPlayoutDevices());
                    break;
            }
        }

        /// <summary>
        /// 利用可能ビデオデバイスのリストをリフレッシュする
        /// </summary>
        /// <param name="videoCaptureDevices">新しく取得した利用可能ビデオデバイスのリスト</param>
        private void RefreshVideoCaptureDevice(IList<MediaDevice> videoCaptureDevices)
        {
            RunOnUiThread(() =>
            {
                // 削除対象のコレクション
                Collection<MediaDevice> videoCaptureDevicesToRemove = new Collection<MediaDevice>();
                // ViewModelが現在保有しているリストと新しいリストを突き合わせて、新しいリストに存在しない場合は削除対象のコレクションにセットする
                foreach (MediaDevice videoCaptureDevice in Cameras)
                {
                    if (videoCaptureDevices.FirstOrDefault(x => x.Id == videoCaptureDevice.Id) == null)
                    {
                        videoCaptureDevicesToRemove.Add(videoCaptureDevice);
                    }
                }
                // 削除対象がfixしたので、ViewModelが管理するリストから削除し、Viewにも反映
                foreach (MediaDevice removedVideoCaptureDevice in videoCaptureDevicesToRemove)
                {
                    if (SelectedCamera != null && SelectedCamera.Id == removedVideoCaptureDevice.Id)
                    {
                        // 現在選択中のカメラが削除されたのならば、いったんnullにしておく
                        SelectedCamera = null;
                    }
                    // CamerasはObservableCollectionなのでViewへ通知が行く
                    Cameras.Remove(removedVideoCaptureDevice);
                }
                // 新しいリストの要素がViewModelが現在保有するリストになければ追加する
                foreach (MediaDevice videoCaptureDevice in videoCaptureDevices)
                {
                    if (Cameras.FirstOrDefault(x => x.Id == videoCaptureDevice.Id) == null)
                    {
                        // CamerasはObservableCollectionなのでViewへ通知が行く
                        Cameras.Add(videoCaptureDevice);
                    }
                }

                // 選択中のカメラが削除されたのならば、リストの先頭のものをセット
                if (SelectedCamera == null)
                {
                    SelectedCamera = Cameras.FirstOrDefault();
                }
            });
        }

        /// <summary>
        /// オーディオデバイス一覧のリフレッシュ
        /// </summary>
        /// <param name="audioCaptureDevices"></param>
        private void RefreshAudioCaptureDevices(IList<MediaDevice> audioCaptureDevices)
        {
            RunOnUiThread(() =>
            {
                var selectedMicrophoneId = SelectedMicrophone?.Id;
                SelectedMicrophone = null;
                Microphones.Clear();
                foreach (MediaDevice audioCaptureDevice in audioCaptureDevices)
                {
                    Microphones.Add(audioCaptureDevice);
                    if (audioCaptureDevice.Id == selectedMicrophoneId)
                    {
                        SelectedMicrophone = Microphones.Last();
                    }
                }

                if (SelectedMicrophone == null)
                {
                    SelectedMicrophone = Microphones.First();
                }
            });
        }

        /// <summary>
        /// スピーカーデバイスのリフレッシュ
        /// </summary>
        /// <param name="audioPlayoutDevices"></param>
        private void RefreshAudioPlayoutDevices(IList<MediaDevice> audioPlayoutDevices)
        {
            RunOnUiThread(() =>
            {
                var selectedPlayoutDeviceId = SelectedAudioPlayoutDevice?.Id;
                SelectedAudioPlayoutDevice = null;
                AudioPlayoutDevices.Clear();
                foreach (MediaDevice audioPlayoutDevice in audioPlayoutDevices)
                {
                    AudioPlayoutDevices.Add(audioPlayoutDevice);
                    if (audioPlayoutDevice.Id == selectedPlayoutDeviceId)
                    {
                        SelectedAudioPlayoutDevice = audioPlayoutDevice;
                    }
                }

                if (SelectedAudioPlayoutDevice == null)
                {
                    SelectedAudioPlayoutDevice = AudioPlayoutDevices.FirstOrDefault();
                }
            });
        }


        #endregion

        #region Properties
        /// <summary>
        /// リモートユーザのリスト
        /// </summary>
        public List<Peer> Peers { get; set; }

        /// <summary>
        /// シグナリングサーバへの接続が完了しているかどうか
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// マイクの使用可否
        /// </summary>
        public bool IsMicrophoneEnabled { get; set; }

        /// <summary>
        /// カメラの使用可否
        /// </summary>
        public bool IsCameraEnabled { get; set; }

        /// <summary>
        /// シグナリングサーバへ接続中かどうか
        /// </summary>
        public bool IsConnecting { get; set; }

        /// <summary>
        /// シグナリングサーバから切断中かどうか
        /// </summary>
        public bool IsDisconnecting { get; set; }

        /// <summary>
        /// 選択中のリモートユーザ
        /// </summary>
        public Peer SelectedPeer { get; set; }


        #endregion

        #region MediaDevice Settings
        /// <summary>
        /// マシン上のカメラデバイスのリスト
        /// </summary>
        public List<MediaDevice> Cameras { get; set; }

        /// <summary>
        /// マイクデバイスのリスト
        /// </summary>
        public List<MediaDevice> Microphones { get; set; }

        /// <summary>
        /// スピーカーデバイスのリスト
        /// </summary>
        public List<MediaDevice> AudioPlayoutDevices { get; set; }

        /// <summary>
        /// 現在選択中のカメラ
        /// </summary>
        public MediaDevice SelectedCamera {
            get => _selectedCamera;
            set {
                _selectedCamera = value;
                if (value == null) return;
                // ビデオキャプチャに使用するビデオデバイスを変更する
                Conductor.Instance.Media.SelectVideoDevice(_selectedCamera);
                if (_allCapRes == null) _allCapRes = new List<string>();
                else _allCapRes.Clear();
                // 選択したデバイスがサポートする解像度とFPSの一覧を取得する(非同期)
                var opRes = value.GetVideoCaptureCapabilities();
                // 取得が完了したあとの処理(非同期)
                opRes.AsTask().ContinueWith(resolutions =>
                {
                    RunOnUiThread(() =>
                    {
                        // opResで例外が発生した場合
                        if (resolutions.IsFaulted)
                        {
                            Exception ex = resolutions.Exception;
                            while (ex is AggregateException && ex.InnerException != null)
                                ex = ex.InnerException;
                            string errorMsg = "SetSelectedCamera: Failed to GetVideoCaptureCapabilities (Error: " + ex.Message + ")";
                            OnStatusMessageUpdate?.Invoke(errorMsg);
                            return;
                        }
                        // 結果が返ってこない
                        if (resolutions.Result == null)
                        {
                            string errorMsg = "SetSelectedCamera: Failed to GetVideoCaptureCapabilities (Result is null)";
                            OnStatusMessageUpdate?.Invoke(errorMsg);
                            return;
                        }
                        // 解像度でグルーピングし、グループの先頭の要素を集めてリスト化
                        var uniqueRes = resolutions.Result.GroupBy(test => test.ResolutionDescription).Select(grp => grp.First()).ToList();
                        // デバイスが640x480の解像度をサポートしていれば、それをデフォルトとする
                        CaptureCapability defaultResolution = null;
                        foreach (var resolution in uniqueRes)
                        {
                            if (defaultResolution == null)
                            {
                                defaultResolution = resolution;
                            }
                            _allCapRes.Add(resolution.ResolutionDescription);
                            if ((resolution.Width == 640) && (resolution.Height == 480))
                            {
                                defaultResolution = resolution;
                            }
                        }
                        SelectedCapResItem = defaultResolution.ResolutionDescription;
                    });
                });
            }
        }
        private MediaDevice _selectedCamera;

        /// <summary>
        /// カメラデバイスがサポートする解像度のリスト(string表現)
        /// </summary>
        public List<string> AllCapRes {
            get => _allCapRes ?? (_allCapRes = new List<string>());
            set => _allCapRes = value;
        }
        private List<string> _allCapRes;

        /// <summary>
        /// 現在選択中のカメラ解像度
        /// </summary>
        public string SelectedCapResItem {
            get => _selectedCapResItem;
            set {
                if (AllCapFps == null) AllCapFps = new List<CaptureCapability>();
                else AllCapFps.Clear();

                if(SelectedCamera != null)
                {
                    // 選択したデバイスから解像度とFPSの一覧を取得する(非同期)
                    var opCap = SelectedCamera.GetVideoCaptureCapabilities();
                    opCap.AsTask().ContinueWith(caps =>
                    {
                        // 設定した解像度がサポートするFPSを抽出してリスト化
                        var fpsList = from cap in caps.Result where cap.ResolutionDescription == value select cap;
                        RunOnUiThread(() =>
                        {
                            CaptureCapability defaultFps = null;
                            uint selectedCapFpsFrameRate = 0;
                            // FPSを設定
                            foreach(var fps in fpsList)
                            {
                                AllCapFps.Add(fps);
                                if(defaultFps == null)
                                {
                                    defaultFps = fps;
                                }
                                SelectedCapFpsItem = defaultFps;
                            }
                        });
                    });
                }
                _selectedCapResItem = value;
            }
        }
        private string _selectedCapResItem = null;

        /// <summary>
        /// カメラデバイスがサポートするFPSのリスト(解像度に依存する)
        /// </summary>
        public List<CaptureCapability> AllCapFps {
            get => _allCapFps ?? (_allCapFps = new List<CaptureCapability>());
            set => _allCapFps = value;
        }
        private List<CaptureCapability> _allCapFps;

        /// <summary>
        /// 現在選択中のビデオFPS
        /// </summary>
        public CaptureCapability SelectedCapFpsItem {
            get => _selectedCapFpsItem;
            set {
                if(_selectedCapFpsItem != value)
                {
                    _selectedCapFpsItem = value;
                    Conductor.Instance.VideoCaptureProfile = value;
                    Conductor.Instance.UpdatePreferredFrameFormat();
                }
            }
        }
        private CaptureCapability _selectedCapFpsItem;

        /// <summary>
        /// 現在選択中のマイクデバイス
        /// </summary>
        public MediaDevice SelectedMicrophone {
            get => _selectedMicrophone;
            set {
                if(_selectedMicrophone != value)
                {
                    _selectedMicrophone = value;
                    if(value != null)
                    {
                        // 使用するマイクデバイスを変更する
                        Conductor.Instance.Media.SelectAudioCaptureDevice(_selectedMicrophone);
                    }
                }
            }
        }
        private MediaDevice _selectedMicrophone;

        /// <summary>
        /// 現在選択中のスピーカーデバイス
        /// </summary>
        public MediaDevice SelectedAudioPlayoutDevice {
            get => _selectedAudioPlayoutDevice;
            set {
                if(_selectedAudioPlayoutDevice != value)
                {
                    _selectedAudioPlayoutDevice = value;
                    if(value != null)
                    {
                        // 使用するスピーカーデバイスを変更する
                        Conductor.Instance.Media.SelectAudioPlayoutDevice(_selectedAudioPlayoutDevice);
                    }
                }
            }
        }
        private MediaDevice _selectedAudioPlayoutDevice;
        #endregion

        private void RunOnUiThread(Action fn)
        {
            var asyncOp = _uiDispathcer.RunAsync(CoreDispatcherPriority.Normal, new DispatchedHandler(fn));
        }
    }
}