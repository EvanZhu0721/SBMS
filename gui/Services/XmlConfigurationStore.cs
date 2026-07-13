using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace SBMSGui
{
    internal interface IConfigurationStore
    {
        bool Exists(string path);
        GuiConfigFile Load(string path);
        void Save(string path, GuiConfigFile config);
    }

    internal sealed class XmlConfigurationStore : IConfigurationStore
    {
        public bool Exists(string path)
        {
            return File.Exists(path);
        }

        public GuiConfigFile Load(string path)
        {
            var serializer = new XmlSerializer(typeof(GuiConfigFile));
            using (FileStream stream = File.OpenRead(path))
            {
                return (GuiConfigFile)serializer.Deserialize(stream);
            }
        }

        public void Save(string path, GuiConfigFile config)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = path + ".tmp";
            var settings = new XmlWriterSettings
            {
                Encoding = Encoding.UTF8,
                Indent = true,
                OmitXmlDeclaration = false
            };
            var serializer = new XmlSerializer(typeof(GuiConfigFile));
            using (XmlWriter writer = XmlWriter.Create(tempPath, settings))
            {
                serializer.Serialize(writer, config);
            }
            File.Copy(tempPath, path, true);
            File.Delete(tempPath);
        }
    }
}
