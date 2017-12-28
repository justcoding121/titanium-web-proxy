namespace Titanium.Web.Proxy.Models
{
    public class SslExtension
    {
        public int Value { get; }

        public string Name { get; }

        public string Data { get; }

        public int Position { get; }

        public SslExtension(int value, string name, string data, int position)
        {
            Value = value;
            Name = name;
            Data = data;
            Position = position;
        }
    }
}