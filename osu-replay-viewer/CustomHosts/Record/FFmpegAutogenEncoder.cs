using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace osu_replay_renderer_netcore.CustomHosts.Record
{
    /// <summary>
    /// Видеоэнкодер на базе ffmpeg-autoGen.
    /// Предоставляет метод WriteFrame, позволяющий передавать указатель на буфер с данными (например, PBO с GPU)
    /// для кодирования без лишнего копирования на CPU.
    /// </summary>
    public unsafe class ExternalFFmpegAutoGenEncoder : IDisposable
    {
        // Настройки видео
        public int FPS { get; set; } = 60;
        public int Width { get; set; }
        public int Height { get; set; }
        public string OutputPath { get; set; } = "output.mp4";

        private AVCodecContext* _codecContext;
        private AVFormatContext* _formatContext;
        private AVStream* _videoStream;
        private AVFrame* _videoFrame;
        private AVPacket* _packet;
        private int _frameIndex = 0;

        private object _locker = new object();

        public ExternalFFmpegAutoGenEncoder(int width, int height)
        {
            Width = width;
            Height = height;
            //ffmpeg.RootPath = @"C:\ffmpeg"; // Путь к библиотекам ffmpeg, если требуется

            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            InitializeFFmpeg();
        }

        private void InitializeFFmpeg()
        {
            // Зарегистрировать кодеки (в новых версиях ffmpeg это может быть не нужно)
            //ffmpeg.av_register_all();

            // Выбираем кодек
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
            if (codec == null)
                throw new ApplicationException("Codec H264 not found.");

            // Создаём контекст кодека
            _codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecContext == null)
                throw new ApplicationException("Could not allocate video codec context.");

            _codecContext->width = Width;
            _codecContext->height = Height;
            _codecContext->time_base = new AVRational { num = 1, den = FPS };
            _codecContext->framerate = new AVRational { num = FPS, den = 1 };
            _codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_RGB24;  // формат входного изображения

            // Настройки энкодера (например, preset, qp, bitrate и т.п.)
            // Например, можно задать опции через AVDictionary, если требуется.

            AVDictionary* codecOptions = null;
            // Пример: ffmpeg.av_dict_set(&codecOptions, "preset", "slow", 0);
            // Пример: ffmpeg.av_dict_set(&codecOptions, "crf", "26", 0);

            ffmpeg.av_dict_set(&codecOptions, "preset", "p7", 0);
            ffmpeg.av_dict_set(&codecOptions, "crf", "26", 0);

            if (ffmpeg.avcodec_open2(_codecContext, codec, &codecOptions) < 0)
                throw new ApplicationException("Could not open codec.");

            // Выделение кадра
            _videoFrame = ffmpeg.av_frame_alloc();
            if (_videoFrame == null)
                throw new ApplicationException("Could not allocate video frame.");
            _videoFrame->format = (int)_codecContext->pix_fmt;
            _videoFrame->width = _codecContext->width;
            _videoFrame->height = _codecContext->height;

            // Выделение буфера для кадра
            int ret = ffmpeg.av_frame_get_buffer(_videoFrame, 32);
            if (ret < 0)
            {
                throw new ApplicationException("Could not allocate the video frame data.");
            }

            // Инициализация формата для записи файла
            fixed (AVFormatContext** avctx = &_formatContext)
            {
                ffmpeg.avformat_alloc_output_context2(avctx, null, null, OutputPath);
            }
            if (_formatContext == null)
                throw new ApplicationException("Could not allocate output context.");

            // Создание видео потока
            _videoStream = ffmpeg.avformat_new_stream(_formatContext, null);
            if (_videoStream == null)
                throw new ApplicationException("Could not create video stream.");

            // Копирование параметров из кодек-контекста в видео поток
            ret = ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _codecContext);
            if (ret < 0)
                throw new ApplicationException("Could not copy the stream parameters.");

            // Открываем выходной файл, если используется файловый вывод
            if ((_formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ret = ffmpeg.avio_open(&_formatContext->pb, OutputPath, ffmpeg.AVIO_FLAG_WRITE);
                if (ret < 0)
                    throw new ApplicationException("Could not open output file.");
            }

            // Записываем заголовки
            ret = ffmpeg.avformat_write_header(_formatContext, null);
            if (ret < 0)
                throw new ApplicationException("Error occurred when opening output file.");

            // Выделяем AVPacket
            _packet = ffmpeg.av_packet_alloc();
            if(_packet == null)
                throw new ApplicationException("Could not allocate AVPacket.");
        }

        /// <summary>
        /// Записывает кадр из внешнего буфера (PBO, GPU). Предполагается, что данные в формате RGB24 и размером Width * Height * 3 байт.
        /// Метод не копирует данные на CPU, только использует указатель, для этого требуется, чтобы вызывающий код обеспечивал корректный lifetime буфера.
        /// </summary>
        /// <param name="buffer">Указатель на буфер с данными</param>
        public void WriteFrame(void* buffer)
        {
            lock (_locker)
            {
                // Обратите внимание: мы не можем напрямую переназначить data[0] AVFrame на указатель GPU, поскольку ffmpeg
                // ожидает, что данные находятся в CPU памяти и управляются его аллокатором.
                // Один из подходов – это создать временную обёртку, которая использует AVBuffer с собственным deallocator,
                // который ничего не делает, но важно следить за временем жизни данных.
                // Приведём пример создания обёртки без лишнего копирования:
                var imageSize = (ulong)(Width * Height * 3); // RGB24

                // Создать AVBufferRef, который оборачивает внешний буфер.
                // Для этого создадим callback, который не удаляет память, поскольку память управляется внешним кодом.
                AVBufferRef* externalRef = ffmpeg.av_buffer_create((byte*)buffer, imageSize, null, null, 0);
                if (externalRef == null)
                    throw new ApplicationException("Could not create external buffer.");

                // Освободим предыдущие данные кадра, если они были
                if (_videoFrame->buf[0] != null)
                {
                    var pBuf = (AVBufferRef**)&_videoFrame->buf; 
                    if (pBuf[0] != null)
                    {
                        ffmpeg.av_buffer_unref(&pBuf[0]);
                    }
                }

                // Устанавливаем указатель на данные кадра без копирования.
                _videoFrame->data[0] = externalRef->data;
                // Не забываем задать strides – для RGB24 stride = ширина * 3.
                _videoFrame->linesize[0] = Width * 3;

                _videoFrame->pts = _frameIndex++;

                // Отправляем кадр в энкодер
                int ret = ffmpeg.avcodec_send_frame(_codecContext, _videoFrame);
                if (ret < 0)
                    throw new ApplicationException("Error sending a frame for encoding.");

                // Получаем все доступные пакеты
                while (ret >= 0)
                {
                    ret = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        break;
                    else if (ret < 0)
                        throw new ApplicationException("Error during encoding.");

                    // Устанавливаем временные метки пакета, соотносим с time_base выходного контекста
                    ffmpeg.av_packet_rescale_ts(_packet, _codecContext->time_base, _videoStream->time_base);
                    _packet->stream_index = _videoStream->index;

                    // Пишем пакет в файл
                    ret = ffmpeg.av_interleaved_write_frame(_formatContext, _packet);
                    if (ret < 0)
                        throw new ApplicationException("Error while writing video frame.");

                    ffmpeg.av_packet_unref(_packet);
                }
            }
        }

        /// <summary>
        /// Финализировать запись: сбросить буферы энкодера и записать trailer файла.
        /// </summary>
        public void Finish()
        {
            lock (_locker)
            {
                // Завершаем энкодирование, отправляя NULL кадр.
                ffmpeg.avcodec_send_frame(_codecContext, null);
                while (ffmpeg.avcodec_receive_packet(_codecContext, _packet) == 0)
                {
                    ffmpeg.av_packet_rescale_ts(_packet, _codecContext->time_base, _videoStream->time_base);
                    _packet->stream_index = _videoStream->index;
                    ffmpeg.av_interleaved_write_frame(_formatContext, _packet);
                    ffmpeg.av_packet_unref(_packet);
                }

                // Записываем trailer файла
                ffmpeg.av_write_trailer(_formatContext);
            }
        }

        public void Dispose()
        {
            Finish();

            if ((_formatContext != null) && ((_formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0))
            {
                ffmpeg.avio_closep(&_formatContext->pb);
            }
            fixed (AVFormatContext** fmtCtx = &_formatContext)
            {
                ffmpeg.avformat_free_context(_formatContext);
            }

            fixed(AVCodecContext** avctx = &_codecContext)
            {
                ffmpeg.avcodec_free_context(avctx);
            }
            
            fixed(AVFrame** avf = &_videoFrame)
            {
                ffmpeg.av_frame_free(avf);
            }
            fixed(AVPacket** avp = &_packet)
            {
                ffmpeg.av_packet_free(avp);
            }
        }
    }
}
