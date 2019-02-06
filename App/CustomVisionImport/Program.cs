using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training;
using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training.Models;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace CustomVisionImport
{
    class Program
    {
        static Dictionary<string, int> tagCounting = new Dictionary<string, int>();
        static Dictionary<string, Guid> tagIdMapping = new Dictionary<string, Guid>();

        static async Task Main(string[] args)
        {
            const string FORMAT_LABELBOX = "labelbox";
            const string FORMAT_CNTK = "cntk";

            var builder = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json");

            var configuration = builder.Build();

            // -------------------------------

            bool online = bool.Parse(configuration["online"]);
            string format = configuration["bboxFormat"];
            if (format == FORMAT_LABELBOX)
            {
                if (Directory.EnumerateFiles(configuration["imagesPath"]).Count() > 0)
                {
                    Console.Write("Download directory is not empty.\nDo you want to re-download images and potentially overwrite existing files? ('y' to accept, Enter to continue)");
                    var input = Console.ReadLine();
                    if (input.ToLowerInvariant() == "y")
                    {
                        await DownloadLabelBox(configuration["labelBoxFile"], configuration["imagesPath"], configuration["imageExtension"]);
                    }
                }
                else
                {
                    await DownloadLabelBox(configuration["labelBoxFile"], configuration["imagesPath"], configuration["imageExtension"]);
                }
            }

            var imageFileEntries = new List<ImageFileCreateEntry>();
            var imageFiles = Directory.EnumerateFiles(configuration["imagesPath"], $"*.{configuration["imageExtension"]}");
            var imageBatches = Batch(imageFiles, 60);

            int fullWidth = 800; // default - will try to detect later
            int fullHeight = 600;

            var client = new CustomVisionTrainingClient()
            {
                ApiKey = configuration["trainingKey"],
                Endpoint = configuration["endpoint"]
            };

            Guid projectId = string.IsNullOrWhiteSpace(configuration["visionProjectId"]) ? Guid.Empty : Guid.Parse(configuration["visionProjectId"]);

            if (online)
            {
                var domains = client.GetDomains();
                var objDetectionDomain = domains.FirstOrDefault(d => d.Type == "ObjectDetection" && d.Exportable == true);

                if (projectId == Guid.Empty)
                {
                    Console.WriteLine("Creating new project.");
                    projectId = client.CreateProject(configuration["projectName"], domainId: objDetectionDomain.Id).Id;
                }
                else
                {
                    var tags = client.GetTags(projectId);
                    tagIdMapping = new Dictionary<string, Guid>(tags.Select((t) => new KeyValuePair<string, Guid>(t.Name, t.Id)));
                    tagCounting = new Dictionary<string, int>(tags.Select((t) => new KeyValuePair<string, int>(t.Name, t.ImageCount)));
                }
            }

            foreach (var batchItem in imageBatches)
            {
                Console.WriteLine("Processing batch...");

                foreach (var fileName in batchItem)
                {
                    var fn = Path.GetFileNameWithoutExtension(fileName);

                    using (System.Drawing.Image measureImg = System.Drawing.Image.FromFile(fileName))
                    {
                        fullWidth = measureImg.Width;
                        fullHeight = measureImg.Height;
                    }

                    Console.WriteLine($"Image: {fn} {fullWidth}x{fullHeight}");

                    var newEntry = new ImageFileCreateEntry()
                    {
                        Name = fn,
                        Contents = File.ReadAllBytes(fileName),
                        Regions = new List<Region>(),
                    };

                    if (format == FORMAT_CNTK)
                    {
                        Console.WriteLine("Processing " + fn);

                        var classes = File.ReadAllLines(Path.Join(configuration["imagesPath"], fn + ".bboxes.labels.tsv"));
                        var boxes = File.ReadAllLines(Path.Join(configuration["imagesPath"], fn + ".bboxes.tsv"));

                        for (int i = 0; i < boxes.Length; i++)
                        {
                            if (string.IsNullOrWhiteSpace(classes[i]))
                                continue;

                            // break boxes and normalize them (0-1)
                            var coords = boxes[i].Split('\t');
                            double left = double.Parse(coords[0], CultureInfo.InvariantCulture) / fullWidth;
                            double top = double.Parse(coords[1], CultureInfo.InvariantCulture) / fullHeight;
                            double width = (double.Parse(coords[2], CultureInfo.InvariantCulture) / fullWidth) - left;
                            double height = (double.Parse(coords[3], CultureInfo.InvariantCulture) / fullHeight) - top;

                            // create tag if needed
                            if (!tagIdMapping.ContainsKey(classes[i]))
                            {
                                if (online)
                                {
                                    var createdTag = client.CreateTag(projectId, classes[i]);
                                    tagIdMapping.Add(createdTag.Name, createdTag.Id);
                                    tagCounting.Add(createdTag.Name, 0);
                                }
                                else
                                {
                                    tagIdMapping.Add(classes[i], Guid.Empty);
                                    tagCounting.Add(classes[i], 0);
                                }

                                Console.WriteLine("New tag " + classes[i]);
                            }

                            // add boxes
                            newEntry.Regions.Add(new Region(tagIdMapping[classes[i]], left, top, width, height));
                            tagCounting[classes[i]]++;
                            Console.WriteLine($"TagCounting: {classes[i]} = {tagCounting[classes[i]]}");
                        }

                        imageFileEntries.Add(newEntry);
                    }

                    if (format == FORMAT_LABELBOX)
                    {
                        var labels = File.ReadAllLines(Path.Join(configuration["imagesPath"], fn + ".txt"));

                        foreach (var l in labels)
                        {
                            var explodedL = l.Split('\t'); // 7->75 9 75 29 87 29 87 9 112 8 112 28 123 28 123 8
                            var labelName = explodedL[0]; // 7
                            var coordinates = explodedL[1].Split(' ');

                            // one label can have multiple rectangles
                            do
                            {
                                var coords = coordinates.Take(8).ToArray(); // 75 9 75 29 87 29 87 9 112 8 112 28 123 28 123 8

                                // no coordinates, skip this label
                                if (string.IsNullOrWhiteSpace(explodedL[1]))
                                    continue;

                                // candidate for top left point
                                double left = double.Parse(coords[0]);
                                double top = double.Parse(coords[1]);

                                // go through the rest of the coords to find if the candidate is really top-left
                                for (int i = 2; i < 8; i += 2)
                                {
                                    if (double.Parse(coords[i]) <= left && double.Parse(coords[i + 1]) <= top)
                                    {
                                        left = double.Parse(coords[i]);
                                        top = double.Parse(coords[i + 1]);
                                    }
                                }

                                // candidate for bottom right point
                                double right = double.Parse(coords[4]);
                                double bottom = double.Parse(coords[5]);

                                // go through the coords to find if the candidate is really bottom-right
                                for (int i = 0; i < 8; i += 2)
                                {
                                    if (double.Parse(coords[i]) >= right && double.Parse(coords[i + 1]) >= bottom)
                                    {
                                        right = double.Parse(coords[i]);
                                        bottom = double.Parse(coords[i + 1]);
                                    }
                                }

                                // normalize for Custom Vision
                                left = left / fullWidth;
                                top = top / fullHeight;
                                right = right / fullWidth;
                                bottom = bottom / fullHeight;

                                // calculate width, height for custom vision
                                double width = Difference(right, left);
                                double height = Difference(bottom, top);

                                if (!tagIdMapping.ContainsKey(labelName))
                                {
                                    if (online)
                                    {
                                        var createdTag = client.CreateTag(projectId, labelName);
                                        tagIdMapping.Add(createdTag.Name, createdTag.Id);
                                        tagCounting.Add(createdTag.Name, 0);
                                    }
                                    else
                                    {
                                        tagIdMapping.Add(labelName, Guid.Empty);
                                        tagCounting.Add(labelName, 0);
                                    }

                                    Console.WriteLine("New tag " + labelName);
                                }

                                // add boxes
                                newEntry.Regions.Add(new Region(tagIdMapping[labelName], left, top, width, height));
                                tagCounting[labelName]++;
                                Console.WriteLine($"TagCounting: {labelName} = {tagCounting[labelName]}");
                            }
                            while ((coordinates = coordinates.Skip(8).ToArray()).Length > 0);
                        }

                        // no boxes, skip this image
                        if (newEntry.Regions.Count == 0)
                            continue;

                        imageFileEntries.Add(newEntry);
                    }

                }

                if (imageFileEntries.Count > 0 && online)
                {
                    Console.WriteLine("Uploading batch...");
                    client.CreateImagesFromFiles(projectId, new ImageFileCreateBatch(imageFileEntries));
                }

                // store everything to a text file for further inspection
                if (bool.Parse(configuration["dumpResults"]))
                {
                    File.AppendAllText(Path.Join(configuration["imagesPath"], "dump.txt"), JsonConvert.SerializeObject(imageFileEntries));
                }

                imageFileEntries.Clear();
            }

            if (bool.Parse(configuration["tagCleanup"]))
            {
                Console.WriteLine("Cleanup...");
                foreach (var tag in tagCounting)
                {
                    // Less than 15 images per tag is not enough for Custom Vision.
                    // Not enough images for this tag => gone.
                    if (tag.Value < 15)
                    {
                        if (online)
                        {
                            Console.WriteLine($"Deleting tag {tag.Key} with {tag.Value} images.");
                            client.DeleteTag(projectId, tagIdMapping[tag.Key]);
                        }
                        else
                        {
                            Console.WriteLine($"Would delete tag {tag.Key} with {tag.Value} images.");
                        }
                    }
                }
            }

            Console.WriteLine("Done.");
            Console.ReadKey();
        }

        static async Task DownloadLabelBox(string sourceJsonPath, string downloadDirPath, string imageExtension)
        {
            Console.WriteLine("Parsing labelbox data...");
            var labelBoxJson = File.ReadAllText(sourceJsonPath);
            var labelBoxExports = JsonConvert.DeserializeObject<LabelBoxExport[]>(labelBoxJson);

            Console.WriteLine("Downloading images.");
            var wc = new WebClient();
            var res = new List<string>();

            Directory.CreateDirectory(downloadDirPath);

            foreach (var labelBoxExport in labelBoxExports)
            {
                if (labelBoxExport.Label is string && (labelBoxExport.Label as string) == "Skip")
                {
                    continue;
                }

                await wc.DownloadFileTaskAsync(new Uri(labelBoxExport.LabeledData), Path.Join(downloadDirPath, $"{labelBoxExport.ID}.{imageExtension}"));

                foreach (var x in (labelBoxExport.Label as JObject))
                {
                    if (!x.Value.HasValues)
                        continue;

                    var obj = x.Value.First["geometry"].ToObject<Geometry[]>();
                    res.Add($"{x.Key}\t{string.Join<Geometry>(' ', obj)}");
                }

                File.WriteAllLines(Path.Join(downloadDirPath, labelBoxExport.ID + ".txt"), res);
                res.Clear();
            }

            wc.Dispose();
        }

        static IEnumerable<IEnumerable<string>> Batch(IEnumerable<string> sourceList, int batchSize)
        {
            string[] bucket = null;
            var count = 0;

            foreach (var file in sourceList)
            {
                if (bucket == null)
                    bucket = new string[batchSize];

                bucket[count++] = file;

                if (count != batchSize)
                    continue;

                yield return bucket;

                bucket = null;
                count = 0;
            }

            if (bucket != null && count > 0)
            {
                yield return bucket.Take(count).ToArray();
            }
        }

        static double Difference(double n1, double n2)
        {
            return n1 > n2 ? n1 - n2 : n2 - n1;
        }
    }
}