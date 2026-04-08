namespace Magic.Kernel.Processor
{
    public sealed class AddressLiteral
    {
        public string Address { get; }

        public AddressLiteral(string address)
        {
            Address = address ?? string.Empty;
        }

        public override string ToString()
            => "&" + Address;
    }
}
