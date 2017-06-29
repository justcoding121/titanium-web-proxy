namespace Titanium.Web.Proxy.Ssl
{
    public class SslExtension
    {
        public int Value { get; set; }

        public string Name { get; set; }

        public string Data { get; set; }

        public SslExtension(int value, string name, string data)
        {
            Value = value;
            Name = name;
            Data = data;
        }
    }
}