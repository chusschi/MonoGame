using System;
using System.Collections.Generic;
using System.Threading;
using System.Collections.Concurrent;

#if MONOMAC
using MonoMac.OpenAL;

#else
using OpenTK.Audio.OpenAL;

#endif
namespace Microsoft.Xna.Framework.Audio {
    public sealed partial class DynamicSoundEffectInstance : SoundEffectInstance {
        const int NUM_BUFFERS = 3;
        ALFormat format;
        ConcurrentQueue<OALSoundBuffer> allocatedBuffers = new ConcurrentQueue<OALSoundBuffer> ();
        ConcurrentQueue<OALSoundBuffer> processedBuffers = new ConcurrentQueue<OALSoundBuffer> ();
        int SourceId;

        void setFormatFor (AudioChannels channels)
        {
            switch (channels) {
            case AudioChannels.Mono:
                this.format = ALFormat.Mono16;
                break;
            case AudioChannels.Stereo:
                this.format = ALFormat.Stereo16;
                break;
            default:
                throw new ArgumentOutOfRangeException ("channels", channels, "Expected Mono or Stereo");
            }                       
        }

        /// <summary>
        /// Preserves the given data buffer by reference and binds its contents to a OALSoundBuffer
        /// that was recycled or newly created. The buffer is then queued for playback.
        /// </summary>
        /// <param name="data">The sound data buffer</param>
        /// <param name="format">The sound buffer data format, e.g. Mono, Mono16 bit, Stereo, etc.</param>
        /// <param name="size">The size of the data buffer</param>
        /// <param name="rate">The sampling rate of the sound effect, e.g. 44 khz, 22 khz.</param>
        private void QueueDataBuffer (byte [] data, ALFormat format, int offset, int size, int rate)
        {

            byte[] dataSubset = new byte[size];
            Array.Copy(data, offset, dataSubset, 0, size);

            OALSoundBuffer soundBuffer;
            if (!processedBuffers.TryDequeue (out soundBuffer)) {
                soundBuffer = new OALSoundBuffer ();
            }

            if (SourceId == 0) {
                bool isSourceAvailable = controller.ReserveSource (soundBuffer);
                if (!isSourceAvailable)
                    throw new InstancePlayLimitException ();
                SourceId = soundBuffer.SourceId;
            } else {
                soundBuffer.SourceId = SourceId;
            }

            allocatedBuffers.Enqueue (soundBuffer);
            soundBuffer.BindDataBuffer (dataSubset, format, size, rate);

            // TODO move AL access to OpenALSoundController
            AL.SourceQueueBuffer (soundBuffer.SourceId, soundBuffer.OpenALDataBuffer);
            
            if (controller.CheckALError("failed to queue buffer")) {
                throw new InvalidOperationException ("failed to queue buffer");
            }
            
            PendingBufferCount++;
        }

        void startPlaying ()
        {
            soundState = SoundState.Playing;

            ThreadPool.QueueUserWorkItem (new WaitCallback (queueBuffersForPlay));
        }

        private void queueBuffersForPlay (Object stateInfo)
        {
            if (allocatedBuffers.Count < NUM_BUFFERS) {
                this.OnBufferNeeded (new EventArgs ());
            }

            OALSoundBuffer nextBuffer;
            var hasNext = allocatedBuffers.TryPeek (out nextBuffer);

            if (!hasNext) {
                Stop ();
                return;
            }

            ApplyState (SourceId);

            AL.SourcePlay (SourceId);

            while (soundState == SoundState.Playing) {

                int processed;
                AL.GetSource (SourceId, ALGetSourcei.BuffersProcessed, out processed);

                PendingBufferCount -= processed;

                while (processed > 0) {
                    OALSoundBuffer soundBuffer;
                    if (!allocatedBuffers.TryDequeue (out soundBuffer)) {
                        soundState = SoundState.Stopped;
                        return;
                    }
                
                    int prevBufferId = soundBuffer.OpenALDataBuffer;
                    int bufferId = soundBuffer.OpenALDataBuffer;
                    AL.SourceUnqueueBuffers (soundBuffer.SourceId, 1, ref bufferId);
                    if (bufferId != prevBufferId)
                    {
                        controller.CheckALError ("failed to unqueue buffer");
                    }

                    processedBuffers.Enqueue (soundBuffer);

                    processed--;
                }

                for(int i=allocatedBuffers.Count;i<NUM_BUFFERS;i++)
                {
                    OnBufferNeeded (new EventArgs ());
                }

                ALSourceState state = AL.GetSourceState (SourceId);
                if ((state == ALSourceState.Stopped) && allocatedBuffers.Count > 0)
                {
                    AL.SourcePlay (SourceId);
                }
                /*if (allocatedBuffers.Count == 0) || state == ALSourceState.Stopped)
                    soundState = SoundState.Stopped;*/

                Thread.Yield ();
            }

			controller.RecycleSource(SourceId);

            SourceId = 0;

			while (allocatedBuffers.Count > 0) {
				OALSoundBuffer b;
				if (allocatedBuffers.TryDequeue (out b))
					b.Dispose ();
				else
					break;
			}

			while (processedBuffers.Count > 0) {
				OALSoundBuffer b;
				if (processedBuffers.TryDequeue (out b))
					b.Dispose ();
				else
					break;
			}

        }
//        /// <summary>
//        /// Stops the current running sound effect, if relevant, removes its event handlers, and disposes
//        /// of the sound buffer.
//        /// </summary>
//        public void Dispose ()
//        {
//            if (!isDisposed)
//            {
//                this.Stop(true);
//                soundBuffer.Reserved -= HandleSoundBufferReserved;
//                soundBuffer.Recycled -= HandleSoundBufferRecycled;
//                soundBuffer.Dispose();
//                soundBuffer = null;
//                isDisposed = true;
//            }
//        }
    }
}
