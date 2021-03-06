﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CSCore;
using CSCore.Codecs.AAC;
using CSCore.Codecs.FLAC;
using CSCore.Codecs.MP3;
using CSCore.Codecs.WAV;
using CSCore.Codecs.WMA;
using CSCore.SoundOut;
using WebDav.AudioPlayer.Client;
using WebDav.AudioPlayer.Models;
using WebDav.AudioPlayer.Util;

namespace WebDav.AudioPlayer.Audio
{
    internal class Player : IDisposable
    {
        private readonly IWebDavClient _client;

        private readonly FixedSizedQueue<ResourceItem> _resourceItemQueue;

        private readonly ISoundOut _soundOut;
        private IWaveSource _waveSource;
        private List<ResourceItem> _items;

        public Action<string> Log;
        public Action<int, ResourceItem> PlayStarted;
        public Action<ResourceItem> PlayPaused;
        public Action<ResourceItem> PlayContinue;
        public Action PlayStopped;

        public List<ResourceItem> Items
        {
            get
            {
                return _items;
            }

            set
            {
                Stop(true);
                _items = value;
            }
        }

        public int SelectedIndex { get; private set; } = -1;

        public PlaybackState PlaybackState => _soundOut?.PlaybackState ?? PlaybackState.Stopped;

        public TimeSpan CurrentTime => _waveSource?.GetPosition() ?? TimeSpan.Zero;

        public TimeSpan TotalTime => _waveSource?.GetLength() ?? TimeSpan.Zero;

        public string SoundOut => _soundOut.GetType().Name;

        public Player(IWebDavClient client)
        {
            _client = client;

            _resourceItemQueue = new FixedSizedQueue<ResourceItem>(3, (resourceItem, size) =>
            {
                Log(string.Format("Disposing : '{0}'", resourceItem.DisplayName));
                if (resourceItem.Stream != null)
                {
                    resourceItem.Stream.Close();
                    resourceItem.Stream.Dispose();
                    resourceItem.Stream = null;
                }
            });

            if (WasapiOut.IsSupportedOnCurrentPlatform)
                _soundOut = new WasapiOut();
            else
                _soundOut = new DirectSoundOut();
        }

        public async void Play(int index, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            bool sameSong = index == SelectedIndex;
            SelectedIndex = index;
            var resourceItem = Items[index];

            // If paused, just unpause and play
            if (PlaybackState == PlaybackState.Paused)
            {
                Pause();
                return;
            }

            // If same song and stream is loaded, just JumpTo start and start play.
            if (sameSong && resourceItem.Stream != null && PlaybackState == PlaybackState.Playing)
            {
                JumpTo(TimeSpan.Zero);
                return;
            }

            Stop(false);

            Log(string.Format(@"Reading : '{0}'", resourceItem.DisplayName));
            var status = await _client.GetStreamAsync(resourceItem, cancellationToken);
            if (status != ResourceLoadStatus.Ok && status != ResourceLoadStatus.StreamExisting)
            {
                Log(string.Format(@"Reading error : {0}", status));
                return;
            }

            Log(string.Format(@"Reading done : {0}", status));

            _resourceItemQueue.Enqueue(resourceItem);

            string extension = new FileInfo(resourceItem.DisplayName).Extension.ToLowerInvariant();
            switch (extension)
            {
                case ".wav":
                    _waveSource = new WaveFileReader(resourceItem.Stream);
                    break;

                case ".mp3":
                    _waveSource = new DmoMp3Decoder(resourceItem.Stream);
                    break;

                case ".ogg":
                    _waveSource = new NVorbisSource(resourceItem.Stream).ToWaveSource();
                    break;

                case ".flac":
                    _waveSource = new FlacFile(resourceItem.Stream);
                    break;

                case ".wma":
                    _waveSource = new WmaDecoder(resourceItem.Stream);
                    break;

                case ".aac":
                case ".m4a":
                case ".mp4":
                    _waveSource = new AacDecoder(resourceItem.Stream);
                    break;

                default:
                    throw new NotSupportedException(string.Format("Extension '{0}' is not supported", extension));
            }

            _soundOut.Initialize(_waveSource);
            _soundOut.Play();
            PlayStarted(SelectedIndex, resourceItem);

            // Preload Next
            PreloadNext(cancellationToken);
        }

        private async void PreloadNext(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            int nextIndex = SelectedIndex + 1;
            if (nextIndex < Items.Count)
            {
                var resourceItem = Items[nextIndex];
                Log(string.Format("Preloading : '{0}'", resourceItem.DisplayName));
                var status = await _client.GetStreamAsync(resourceItem, cancellationToken);
                if (status != ResourceLoadStatus.Ok && status != ResourceLoadStatus.StreamExisting)
                {
                    Log(string.Format(@"Preloading error : {0}", status));
                    return;
                }

                Log(string.Format(@"Preloading done : {0}", status));
                _resourceItemQueue.Enqueue(resourceItem);
            }
        }

        public void Next(CancellationToken cancelAction)
        {
            int nextIndex = SelectedIndex + 1;
            if (nextIndex < Items.Count)
                Play(nextIndex, cancelAction);
            else
                Stop(true);
        }

        public void Previous(CancellationToken cancelAction)
        {
            int previousIndex = SelectedIndex - 1;
            if (previousIndex >= 0)
                Play(previousIndex, cancelAction);
            else
                Stop(true);
        }

        public void Stop(bool force)
        {
            if (_soundOut != null)
            {
                _soundOut.Stop();
                PlayStopped();

                if (force)
                    _resourceItemQueue.Clear();
            }

            if (_waveSource != null)
            {
                _waveSource.Dispose();
                _waveSource = null;
            }
        }

        public void Pause()
        {
            var resourceItem = Items[SelectedIndex];

            if (PlaybackState == PlaybackState.Playing)
            {
                _soundOut.Pause();
                PlayPaused(resourceItem);
            }
            else if (PlaybackState == PlaybackState.Paused)
            {
                _soundOut.Play();
                PlayContinue(resourceItem);
            }
        }

        public void JumpTo(TimeSpan position)
        {
            if (PlaybackState == PlaybackState.Playing)
            {
                _waveSource.SetPosition(position);
            }
        }

        public void SetVolume(float volume)
        {
            if (PlaybackState == PlaybackState.Playing)
            {
                _soundOut.Volume = volume;
            }
        }

        public void Dispose()
        {
            Stop(true);
            _soundOut.Dispose();
        }
    }
}