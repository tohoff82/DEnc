﻿using DEnc.Commands;
using DEnc.Encode;
using DEnc.Exceptions;
using DEnc.Models;
using DEnc.Models.Interfaces;
using DEnc.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DEnc
{
    /// <summary>
    /// A construct for performing encode functions.
    /// </summary>
    public class Encoder
    {
        private readonly Action<string> stderrLog;

        private readonly Action<string> stdoutLog;

        /// <summary>
        /// Creates a new encoder with the given paths for ffmpeg and MP4Box, as well as the working directory.
        /// The given pointers to ffmpeg and MP4Box are tested by executing them with no parameters upon construction. An exception is thrown if the execution fails.
        /// </summary>
        /// <param name="ffmpegPath">A full path or environmental variable for ffmpeg.</param>
        /// <param name="ffprobePath">A full path or environmental variable for ffprobe.</param>
        /// <param name="mp4BoxPath">A full path or environmental variable for MP4Box.</param>
        ///<param name="stdoutLog">A callback which reflects stdout of ffmpeg/MP4Box. May be left null.</param>
        ///<param name="stderrLog">A callback used for logging, and for the stderr of ffmpeg/MP4Box. May be left null.</param>
        /// <param name="workingDirectory">A directory to generate output files in. If null, a temp path is used.</param>
        /// <param name="ffmpegCommandGenerator">A function which generates the ffmpeg command from the configuration and the input file data. A good default is provided if left null.</param>
        /// <param name="mp4BoxCommandGenerator">A function which generates the MP4Box command from the configuration and input video/audio streams. A good default is provided if left null.</param>
        public Encoder(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe", string mp4BoxPath = "MP4Box",
            Action<string> stdoutLog = null, Action<string> stderrLog = null, string workingDirectory = null,
            Func<DashConfig, MediaMetadata, FFmpegCommand> ffmpegCommandGenerator = null,
            Func<DashConfig, IEnumerable<StreamVideoFile>, IEnumerable<StreamAudioFile>, Mp4BoxRenderedCommand> mp4BoxCommandGenerator = null)
        {
            FFmpegPath = ffmpegPath;
            FFprobePath = ffprobePath;
            Mp4BoxPath = mp4BoxPath;
            this.stdoutLog = stdoutLog ?? new Action<string>((s) => { });
            this.stderrLog = stderrLog ?? new Action<string>((s) => { });
            WorkingDirectory = workingDirectory ?? Path.GetTempPath();
            FFmpegCommandGenerator = ffmpegCommandGenerator ?? GenerateFFmpegCommand;
            Mp4BoxCommandGenerator = mp4BoxCommandGenerator ?? GenerateMp4BoxCommand;

            if (!Directory.Exists(WorkingDirectory))
            {
                throw new DirectoryNotFoundException("The given path for the working directory doesn't exist.");
            }
        }

        /// <summary>
        /// A function which generates the ffmpeg command from the configuration and the input file data. A default is provided if not given.
        /// </summary>
        public Func<DashConfig, MediaMetadata, FFmpegCommand> FFmpegCommandGenerator { get; private set; } = GenerateFFmpegCommand;

        /// <summary>
        /// The path to ffmpeg.
        /// </summary>
        public string FFmpegPath { get; private set; }

        /// <summary>
        /// The path to ffprobe.
        /// </summary>
        public string FFprobePath { get; private set; }

        /// <summary>
        /// A function which generates the MP4Box command from the configuration and video/audio streams. A default is provided if not given.
        /// </summary>
        public Func<DashConfig, IEnumerable<StreamVideoFile>, IEnumerable<StreamAudioFile>, Mp4BoxRenderedCommand> Mp4BoxCommandGenerator { get; private set; } = GenerateMp4BoxCommand;

        /// <summary>
        /// The path to MP4Box.
        /// </summary>
        public string Mp4BoxPath { get; private set; }

        /// <summary>
        /// The temp path to store encodes in progress.
        /// </summary>
        public string WorkingDirectory { get; private set; }

        /// <summary>
        /// The default function for generating an ffmpeg command.
        /// </summary>
        public static FFmpegCommand GenerateFFmpegCommand(DashConfig config, MediaMetadata inputStats)
        {
            return FFmpegCommandBuilder
                .Initilize(
                    inPath: config.InputFilePath,
                    outDirectory: config.OutputDirectory,
                    outBaseFilename: config.OutputFileName,
                    options: config.Options,
                    enableStreamCopying: config.EnableStreamCopying
                 )
                .WithVideoCommands(inputStats.VideoStreams, config.Qualities, config.Framerate, config.KeyframeInterval, inputStats.KBitrate)
                .WithAudioCommands(inputStats.AudioStreams)
                .WithSubtitleCommands(inputStats.SubtitleStreams)
                .Build();
        }

        /// <summary>
        /// The default function for generating an MP4Box command.
        /// </summary>
        public static Mp4BoxRenderedCommand GenerateMp4BoxCommand(DashConfig config, IEnumerable<StreamVideoFile> videoFiles, IEnumerable<StreamAudioFile> audioFiles)
        {
            // Use a default key interval of 3s if a framerate or keyframe interval is not given.
            int keyInterval = (config.KeyframeInterval == 0 || config.Framerate == 0) ? 3000 : (config.KeyframeInterval / config.Framerate * 1000);
            string mpdOutputPath = Path.Combine(config.OutputDirectory, config.OutputFileName) + ".mpd";
            var mp4boxCommand = Mp4BoxCommandBuilder.BuildMp4boxMpdCommand(
                videoFiles: videoFiles,
                audioFiles: audioFiles,
                mpdOutputPath: mpdOutputPath,
                keyInterval: keyInterval,
                additionalFlags: config.Options.AdditionalMP4BoxFlags);
            return mp4boxCommand;
        }


        /// <summary>
        /// Re-encodes and splits an input media file into individual stream files for DASHing.
        /// </summary>
        /// <param name="config">Configuration on which file to encode and how to perform the encoding.</param>
        /// <param name="inputStats">Stats on the input file, usually retrieved with <see cref="ProbeFile"/></param>
        /// <param name="progress">A progress event which is fed from the ffmpeg process. Tracks encoding progress.</param>
        /// <param name="cancel">A cancellation token which can be used to end the encoding process prematurely.</param>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tried action is best effort mitigation")]
        public FFmpegCommand EncodeVideo(DashConfig config, MediaMetadata inputStats, IProgress<double> progress = null, CancellationToken cancel = default)
        {
            FFmpegCommand ffmpegCommand = null;
            var log = new StringBuilder();
            try
            {
                ffmpegCommand = FFmpegCommandGenerator(config, inputStats);

                ExecutionResult ffResult;
                ffResult = ManagedExecution.Start(FFmpegPath, ffmpegCommand.RenderedCommand,
                    (x) =>
                    {
                        log.AppendLine(x);
                        stdoutLog.Invoke(x);
                    },
                    (x) =>
                    {
                        log.AppendLine(x);
                        FFmpegProgressShim(x, inputStats.Duration, progress);
                    }, cancel);

                // Detect error in ffmpeg process and cleanup, then return null.
                if (ffResult.ExitCode != 0)
                {
                    throw new FFMpegFailedException(ffmpegCommand, log, $"ERROR: ffmpeg returned code {ffResult.ExitCode}. File: {config.InputFilePath}");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    CleanFiles(ffmpegCommand.AllPieces.Select(x => x.Path));
                }
                catch (Exception)
                {
                }
                if (ex is FFMpegFailedException) { throw; }
                throw new FFMpegFailedException(ffmpegCommand, log, ex.Message, ex);
            }
            finally
            {

            }

            return ffmpegCommand;
        }

        /// <summary>
        /// Obsolete
        /// </summary>
        [Obsolete("This method has been replaced by a new API", true)]
        public DashEncodeResult GenerateDash(string inFile, string outFilename, int framerate, int keyframeInterval,
            IEnumerable<IQuality> qualities, IEncodeOptions options = null, string outDirectory = null, IProgress<IEnumerable<EncodeStageProgress>> progress = null, CancellationToken cancel = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Converts the input file into an MPEG DASH representations.
        /// This includes multiple bitrates, subtitle tracks, audio tracks, and an MPD manifest.
        /// </summary>
        /// <param name="config">A configuration specifying how DASHing should be performed.</param>
        /// <param name="probedInputData">The output from running <see cref="ProbeFile"/> on the input file.</param>
        /// <param name="progress">Gives progress through the ffmpeg process, which takes the longest of all the parts of DASHing.</param>
        /// <param name="cancel">Allows the process to be ended part way through.</param>
        /// <returns>A value containing metadata about the artifacts of the DASHing process.</returns>
        /// <exception cref="DirectoryNotFoundException">The working directory for this class instance doesn't exist.</exception>
        /// <exception cref="ArgumentNullException">The probe data parameter is null.</exception>
        /// <exception cref="FFMpegFailedException">The ffmpeg process returned an error code other than 0 or threw an inner exception such as <see cref="OperationCanceledException"/>.</exception>
        /// <exception cref="Mp4boxFailedException">The MP4Box process returned an error code other than 0, threw an inner exception such as <see cref="OperationCanceledException"/>, or did not generate an MPD file.</exception>
        /// <exception cref="DashManifestNotCreatedException">Everything seemed to go okay until the final step with MP4Box, where an MPD file was not found.</exception>
        public DashEncodeResult GenerateDash(DashConfig config, MediaMetadata probedInputData, IProgress<double> progress = null, CancellationToken cancel = default)
        {
            cancel.ThrowIfCancellationRequested();

            if (!Directory.Exists(WorkingDirectory))
            {
                throw new DirectoryNotFoundException("The given path for the working directory doesn't exist.");
            }

            if (probedInputData == null) { throw new ArgumentNullException(nameof(probedInputData), "Probe data cannot be null. Get this parameter from calling ProbeFile."); }

            //Field declarations
            IQuality compareQuality;
            bool enableStreamCopy = false;

            if (!config.DisableQualityCrushing)
            {
                config.Qualities = QualityCrusher.CrushQualities(config.Qualities, probedInputData.KBitrate);
            }
            compareQuality = config.Qualities.First();

            if (config.EnableStreamCopying && compareQuality.Bitrate == 0)
            {
                enableStreamCopy = Copyable264Infer.DetermineCopyCanBeDone(compareQuality.PixelFormat, compareQuality.Level, compareQuality.Profile.ToString(), probedInputData.VideoStreams);
            }

            // Set the framerate interval to match input if user has not already set
            if (config.Framerate <= 0)
            {
                config.Framerate = (int)Math.Round(probedInputData.Framerate);
            }

            // Set the keyframe interval to match input if user has not already set
            if (config.KeyframeInterval <= 0)
            {
                config.KeyframeInterval = config.Framerate * 3;
            }

            cancel.ThrowIfCancellationRequested();

            FFmpegCommand ffmpegCommand = EncodeVideo(config, probedInputData, progress, cancel);

            Mp4BoxRenderedCommand mp4BoxCommand = GenerateDashManifest(config, ffmpegCommand.VideoPieces, ffmpegCommand.AudioPieces, cancel, ffmpegCommand);

            if (File.Exists(mp4BoxCommand.MpdPath))
            {
                int maxFileIndex = ffmpegCommand.AllPieces.Max(x => x.Index);
                IEnumerable<StreamSubtitleFile> allSubtitles = ProcessSubtitles(config, ffmpegCommand.SubtitlePieces, maxFileIndex + 1);
                MPD mpd = PostProcessMpdFile(mp4BoxCommand.MpdPath, allSubtitles);

                return new DashEncodeResult(mp4BoxCommand.MpdPath, mpd, ffmpegCommand);
            }

            throw new DashManifestNotCreatedException(mp4BoxCommand.MpdPath, ffmpegCommand, mp4BoxCommand,
                $"MP4Box did not produce the expected mpd file at path {mp4BoxCommand.MpdPath}. File: {config.InputFilePath}");
        }

        /// <summary>
        /// Runs ffprobe on a given file on disk, and returns an interpreted result of the ffprobe data as well as the entire dataset.
        /// </summary>
        /// <param name="inFile">The media file to probe.</param>
        /// <param name="rawProbe">The complete data from the ffprobe process.</param>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Default is used in case of unexpected input")]
        public MediaMetadata ProbeFile(string inFile, out FFprobeData rawProbe)
        {
            string args = $"-print_format xml=fully_qualified=1 -show_format -show_streams -- \"{inFile}\"";
            var exResult = ManagedExecution.Start(FFprobePath, args);

            string xmlData = string.Join("\n", exResult.Output);

            if (FFprobeData.Deserialize(xmlData, out rawProbe))
            {
                List<MediaStream> audioStreams = new List<MediaStream>();
                List<MediaStream> videoStreams = new List<MediaStream>();
                List<MediaStream> subtitleStreams = new List<MediaStream>();
                foreach (var s in rawProbe.streams)
                {
                    switch (s.codec_type)
                    {
                        case "audio":
                            audioStreams.Add(s);
                            break;

                        case "video":
                            videoStreams.Add(s);
                            break;

                        case "subtitle":
                            subtitleStreams.Add(s);
                            break;

                        default:
                            break;
                    }
                }

                var metadata = new Dictionary<string, string>();
                if (rawProbe.format.tag != null)
                {
                    foreach (var item in rawProbe.format.tag)
                    {
                        if (!metadata.ContainsKey(item.key))
                        {
                            metadata.Add(item.key.ToLower(System.Globalization.CultureInfo.InvariantCulture), item.value);
                        }
                    }
                }

                var firstVideoStream = videoStreams.FirstOrDefault(x => Constants.SupportedInputCodecs.ContainsKey(x.codec_name)) ?? videoStreams.First();

                decimal framerate = 0;
                long bitrate = 0;
                if (firstVideoStream == null)
                {
                    // Leave them as zero.
                }
                else
                {
                    if (decimal.TryParse(firstVideoStream.r_frame_rate, out framerate)) { }
                    else if (firstVideoStream.r_frame_rate.Contains("/"))
                    {
                        try
                        {
                            framerate = firstVideoStream.r_frame_rate
                                .Split('/')
                                .Select(component => decimal.Parse(component))
                                .Aggregate((dividend, divisor) => dividend / divisor);
                        }
                        catch (Exception)
                        {
                            // Leave it as zero.
                        }
                    }

                    bitrate = firstVideoStream.bit_rate != 0 ? firstVideoStream.bit_rate : (rawProbe.format?.bit_rate ?? 0);
                }

                float duration = rawProbe.format != null ? rawProbe.format.duration : 0;

                var meta = new MediaMetadata(inFile, videoStreams, audioStreams, subtitleStreams, metadata, bitrate, framerate, duration);
                return meta;
            }

            return null;
        }

        /// <summary>
        /// Returns the best guess at the three character language code for the given vtt file. Defaults to "und".
        /// </summary>
        protected static string GetSubtitleName(string vttFilename)
        {
            if (vttFilename.Contains("."))
            {
                var dotComponents = vttFilename.Split('.');
                if (dotComponents.Length > 2)
                {
                    var possibleLang = dotComponents.Skip(1).Take(dotComponents.Length - 2);
                    foreach (var component in possibleLang)
                    {
                        if (Constants.Languages.TryGetValue(component, out string languageName))
                        {
                            return languageName;
                        }
                    }
                }
            }
            return "und";
        }

        /// <summary>
        /// Processes the media subtitles and finds and handles external subtitle files
        /// </summary>
        /// <param name="config">The <see cref="DashConfig"/></param>
        /// <param name="subtitleFiles">The subtitle stream files</param>
        /// <param name="startFileIndex">The index additional subtitles need to start at. This should be the max index of the ffmpeg pieces +1</param>
        protected static IEnumerable<StreamSubtitleFile> ProcessSubtitles(DashConfig config, IEnumerable<StreamSubtitleFile> subtitleFiles, int startFileIndex)
        {
            // Move subtitles found in media
            foreach (var subFile in subtitleFiles)
            {
                string oldPath = subFile.Path;
                subFile.Path = Path.Combine(config.OutputDirectory, Path.GetFileName(subFile.Path));
                yield return subFile;
                if (oldPath != subFile.Path)
                {
                    if (File.Exists(subFile.Path))
                    {
                        File.Delete(subFile.Path);
                    }
                    File.Move(oldPath, subFile.Path);
                }
            }

            // Add external subtitles
            string baseFilename = Path.GetFileNameWithoutExtension(config.InputFilePath);
            foreach (var vttFile in Directory.EnumerateFiles(Path.GetDirectoryName(config.InputFilePath), baseFilename + "*", SearchOption.TopDirectoryOnly))
            {
                if (vttFile.EndsWith(".vtt"))
                {
                    string vttFilename = Path.GetFileName(vttFile);
                    string vttName = GetSubtitleName(vttFilename);
                    string vttOutputPath = Path.Combine(config.OutputDirectory, $"{config.OutputFileName}_subtitle_{vttName}_{startFileIndex}.vtt");

                    var subFile = new StreamSubtitleFile()
                    {
                        Index = startFileIndex,
                        Path = vttOutputPath,
                        Language = $"{vttName}_{startFileIndex}"
                    };
                    startFileIndex++;
                    File.Copy(vttFile, vttOutputPath, true);
                    yield return subFile;
                }
            }
        }

        /// <summary>
        /// Removes the set of paths from disk.
        /// </summary>
        /// <param name="paths">A set of absolute paths.</param>
        protected void CleanFiles(IEnumerable<string> paths)
        {
            var failures = Utilities.DeleteFilesFromDisk(paths);
            foreach (var path in paths)
            {
                var failed = failures.Where(x => x.Path == path).FirstOrDefault();
                if (failed == default)
                {
                    stderrLog.Invoke($"Deleted file {path}");
                }
                else
                {
                    stderrLog.Invoke($"Failed to delete file {path} Exception: {failed.Ex}");
                }
            }
        }

        /// <summary>
        /// This method takes configuration, and a set of video and audio streams, and assemb
        /// </summary>
        /// <param name="config">The config to use to generate the MP4Box command.</param>
        /// <param name="videoFiles">A set of video files to include in the DASH process and manifest.</param>
        /// <param name="audioFiles">A set of audio files to include in the DASH process and manifest.</param>
        /// <param name="cancel">A cancel token to pass to the process.</param>
        /// <param name="originalFFmpegCommand">The ffmpeg command used to create the input files. This is for exception logging only, and may be left null.</param>
        protected Mp4BoxRenderedCommand GenerateDashManifest(DashConfig config, IEnumerable<StreamVideoFile> videoFiles, IEnumerable<StreamAudioFile> audioFiles, CancellationToken cancel, FFmpegCommand originalFFmpegCommand = null)
        {
            Mp4BoxRenderedCommand mp4boxCommand = null;
            ExecutionResult mpdResult;
            var log = new StringBuilder();
            try
            {
                mp4boxCommand = Mp4BoxCommandGenerator(config, videoFiles, audioFiles);
                mpdResult = ManagedExecution.Start(Mp4BoxPath, mp4boxCommand.RenderedCommand,
                    (x) =>
                    {
                        log.AppendLine(x);
                        stdoutLog.Invoke(x);
                    },
                    (x) =>
                    {
                        log.AppendLine(x);
                        stderrLog.Invoke(x);
                    }, cancel);

                if (mpdResult.ExitCode != 0)
                {
                    try
                    {
                        // Error in MP4Box.
                        if (File.Exists(mp4boxCommand.MpdPath))
                        {
                            MPD mpdFile = MPD.LoadFromFile(mp4boxCommand.MpdPath);
                            var filePaths = mpdFile.GetFileNames().Select(x => Path.Combine(config.OutputDirectory, x));

                            CleanFiles(filePaths);
                            CleanFiles(mpdResult.Output);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new Mp4boxFailedException(originalFFmpegCommand, mp4boxCommand, log, $"MP4Box returned code {mpdResult.ExitCode}.", ex);
                    }
                    throw new Mp4boxFailedException(originalFFmpegCommand, mp4boxCommand, log, $"MP4Box returned code {mpdResult.ExitCode}.");
                }
                else if (!File.Exists(mp4boxCommand.MpdPath))
                {
                    throw new Mp4boxFailedException(originalFFmpegCommand, mp4boxCommand, log, $"MP4Box appeared to succeed, but no MPD file was created.");
                }
            }
            catch (Exception ex)
            {
                if (ex is Mp4boxFailedException) { throw; }
                throw new Mp4boxFailedException(originalFFmpegCommand, mp4boxCommand, log, ex.Message, ex);
            }
            finally
            {
                CleanFiles(videoFiles.Select(x => x.Path));
                CleanFiles(audioFiles.Select(x => x.Path));
            }

            return mp4boxCommand;
        }
        /// <summary>
        /// Performs on-disk post processing of the generated MPD file.
        /// Subtitles are added, useless tags removed, etc.
        /// </summary>
        private static MPD PostProcessMpdFile(string filepath, IEnumerable<StreamSubtitleFile> subtitles)
        {
            MPD.TryLoadFromFile(filepath, out MPD mpd, out Exception ex);
            mpd.ProgramInformation = null;

            // Get the highest used representation ID so we can increment it for new IDs.
            int.TryParse(mpd.Period.Max(x => x.AdaptationSet.Max(y => y.Representation.Max(z => z.Id))), out int representationId);
            representationId++;

            foreach (var period in mpd.Period)
            {
                // Add subtitles to this period.
                foreach (var sub in subtitles)
                {
                    period.AdaptationSet.Add(new AdaptationSet()
                    {
                        MimeType = "text/vtt",
                        Lang = sub.Language,
                        ContentType = "text",
                        Representation = new List<Representation>()
                        {
                            new Representation()
                            {
                                Id = representationId.ToString(),
                                Bandwidth = 256,
                                BaseURL = new List<string>()
                                {
                                    Path.GetFileName(sub.Path)
                                }
                            }
                        },
                        Role = new DescriptorType()
                        {
                            SchemeIdUri = "urn:gpac:dash:role:2013",
                            Value = $"{sub.Language} {representationId}"
                        }
                    });
                    representationId++;
                }
            }

            mpd.SaveToFile(filepath);
            return mpd;
        }

        private void FFmpegProgressShim(string ffmpegLogLine, float fileDuration, IProgress<double> progress)
        {
            if (ffmpegLogLine != null)
            {
                var match = Regexes.ParseProgress.Match(ffmpegLogLine);
                if (match.Success && TimeSpan.TryParse(match.Value, out TimeSpan p))
                {
                    stdoutLog(ffmpegLogLine);
                    float progressFloat = Math.Min(1, (float)(p.TotalMilliseconds / 1000) / fileDuration);
                    if (progress != null)
                    {
                        progress.Report(progressFloat);
                    }
                }
                else
                {
                    stderrLog(ffmpegLogLine);
                }
            }
            else
            {
                stderrLog(ffmpegLogLine);
            }
        }
    }
}