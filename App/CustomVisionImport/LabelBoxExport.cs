using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace CustomVisionImport
{

    public class LabelBoxExport
    {
        public string ID { get; set; }
        [JsonProperty("Labeled Data")]
        public string LabeledData { get; set; }
        public object Label { get; set; }
        public string CreatedBy { get; set; }
        public string ProjectName { get; set; }
        public DateTime CreatedAt { get; set; }
        public float SecondstoLabel { get; set; }
        public string ExternalID { get; set; }
        public object Agreement { get; set; }
        public string DatasetName { get; set; }
        public object[] Reviews { get; set; }
        public string ViewLabel { get; set; }
    }

    public class Geometry
    {
        public int x { get; set; }
        public int y { get; set; }

        public override string ToString()
        {
            return $"{x} {y}";
        }
    }

    public class Label
    {
        public Geometry[] Geometries { get; set; }
    }

}
