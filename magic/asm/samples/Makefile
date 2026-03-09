# Magic ASM samples — build output.o, output.elf, output.bin
# Стандартная сборка:  make   → output.bin из hello.masm
# Дамп для сравнения: make dump   или  make build-dump

MASM      = hello.masm
OBJ       = output.o
ELF       = output.elf
BIN       = output.bin
LDSCRIPT  = script.ld

# Run make from this directory (samples/). magic-asm is in parent (magic/asm/).
MAGIC_ASM := ../magic-asm

.PHONY: all run run-hd run-window clean call_ret.bin run-call-ret dump dump-call-ret build-dump disasm disasm-call-ret

all: $(BIN)

$(MAGIC_ASM):
	$(MAKE) -C ..

$(OBJ): $(MASM) $(MAGIC_ASM)
	@echo "[MAGIC-ASM] $(MASM) -> $(OBJ)"
	$(MAGIC_ASM) $(MASM) $(OBJ)

$(ELF): $(OBJ) $(LDSCRIPT)
	@echo "[LD] $(ELF)"
	ld -m elf_i386 -T $(LDSCRIPT) -o $(ELF) $(OBJ) -N

$(BIN): $(ELF)
	@echo "[OBJCOPY] $(BIN)"
	objcopy -O binary -j .text $(ELF) $(BIN)

QEMU := $(shell command -v qemu-system-i386 2>/dev/null || command -v qemu-system-x86_64 2>/dev/null || command -v qemu 2>/dev/null || true)

run: $(BIN)
	@if [ -z "$(QEMU)" ]; then echo "QEMU not found. Install: sudo apt install qemu-system-x86"; exit 1; fi
	@echo "=== QEMU in THIS terminal (no window). Serial 'D' and BIOS text here. Exit: Ctrl+A X ==="
	$(QEMU) -drive format=raw,file=$(BIN),if=ide,index=0,media=disk -boot order=c -nographic -display none -serial mon:stdio

run-window: $(BIN)
	@if [ -z "$(QEMU)" ]; then echo "QEMU not found."; exit 1; fi
	@echo "QEMU window (VGA). Serial not in terminal."
	$(QEMU) -drive format=raw,file=$(BIN),if=ide,index=0,media=disk -boot order=c

# Второй пример: call/ret для отладки
call_ret.o: call_ret.masm $(MAGIC_ASM)
	@echo "[MAGIC-ASM] call_ret.masm -> call_ret.o"
	$(MAGIC_ASM) call_ret.masm call_ret.o

call_ret.elf: call_ret.o $(LDSCRIPT)
	@echo "[LD] call_ret.elf"
	ld -m elf_i386 -T $(LDSCRIPT) -o call_ret.elf call_ret.o -N

call_ret.bin: call_ret.elf
	@echo "[OBJCOPY] call_ret.bin"
	objcopy -O binary -j .text call_ret.elf call_ret.bin

run-call-ret: call_ret.bin
	@if [ -z "$(QEMU)" ]; then echo "QEMU not found."; exit 1; fi
	@echo "QEMU window (VGA). Exit: Ctrl+Alt+G then close."
	$(QEMU) -drive format=raw,file=call_ret.bin,if=ide,index=0,media=disk -boot order=c

# Дамп .bin для отладки (скопировать вывод и скинуть для сравнения)
dump: $(BIN)
	@echo "=== xxd $(BIN) (from $(MASM)) ==="
	xxd $(BIN) || hexdump -C $(BIN)

# Сборка + дамп одной командой (стандартный make → output.bin)
build-dump: $(BIN)
	@echo "=== xxd $(BIN) (from $(MASM)) ==="
	xxd $(BIN) || hexdump -C $(BIN)

dump-call-ret: call_ret.bin
	@echo "=== xxd call_ret.bin ==="
	xxd call_ret.bin || hexdump -C call_ret.bin

# Дизассемблирование: инструкции вместо дампа (ndisasm 16-bit, base 0x7C00 как в загрузчике)
disasm: $(BIN)
	@echo "=== ndisasm -b 16 -o 0x7C00 $(BIN) ==="
	ndisasm -b 16 -o 0x7C00 $(BIN) 2>/dev/null || objdump -b binary -m i8086 -D $(BIN)

disasm-call-ret: call_ret.bin
	@echo "=== ndisasm -b 16 -o 0x7C00 call_ret.bin ==="
	ndisasm -b 16 -o 0x7C00 call_ret.bin 2>/dev/null || objdump -b binary -m i8086 -D call_ret.bin

clean:
	rm -f $(OBJ) $(ELF) $(BIN) call_ret.o call_ret.elf call_ret.bin
