namespace Magic.Kernel.Compilation.Ast
{
    /// <summary>Пара слотов памяти [aggregate, delta] для привязки аргументов acall/call на метку streamwait_loop_*_delta.</summary>
    public sealed class StreamWaitDeltaBindSlotsParameterNode : ParameterNode
    {
        public long AggregateSlot { get; set; }
        public long DeltaSlot { get; set; }

        /// <summary>Слоты кадра тела цикла: значения со стека (аргументы 2..) записываются в Local[slot] в том же порядке.</summary>
        public long[] CaptureToSlots { get; set; } = System.Array.Empty<long>();
    }
}
