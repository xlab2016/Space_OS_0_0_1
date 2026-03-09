/*
 * Magic ASM - extended assembler, emits ELF .o directly (no external as).
 * Compiles: gcc -O2 -o magic-asm main.c
 */

#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdint.h>

/* --- ELF32 (i386) --- */
#define ELF32_ST_INFO(b,t) (((b)<<4)+((t)&0xF))
#define ELF32_ST_BIND(i)   ((i)>>4)
#define ELF32_ST_TYPE(i)   ((i)&0xF)
#define STB_GLOBAL 1
#define STT_FUNC    2
#define STT_OBJECT  1
#define STT_NOTYPE  0
#define SHN_ABS     0xFFF1
#define R_386_16    20
#define R_386_PC16  21
#define R_386_PC8   22
#define ET_REL      1
#define EM_386      3
#define SHT_PROGBITS 1
#define SHT_STRTAB   3
#define SHT_REL      9
#define SHT_SYMTAB   2
#define SHF_ALLOC    2
#define SHF_EXECINSTR 4

typedef uint32_t Elf32_Addr;
typedef uint16_t Elf32_Half;
typedef uint32_t Elf32_Off;
typedef int32_t  Elf32_Sword;
typedef uint32_t Elf32_Word;

/* Packed so 64-bit host produces correct 32-bit ELF layout (no padding) */
typedef struct __attribute__((packed)) {
	unsigned char e_ident[16];
	Elf32_Half e_type;
	Elf32_Half e_machine;
	Elf32_Word e_version;
	Elf32_Addr e_entry;
	Elf32_Off  e_phoff;
	Elf32_Off  e_shoff;
	Elf32_Word e_flags;
	Elf32_Half e_ehsize;
	Elf32_Half e_phentsize;
	Elf32_Half e_phnum;
	Elf32_Half e_shentsize;
	Elf32_Half e_shnum;
	Elf32_Half e_shstrndx;
} Elf32_Ehdr;

typedef struct __attribute__((packed)) {
	Elf32_Word sh_name;
	Elf32_Word sh_type;
	Elf32_Word sh_flags;
	Elf32_Addr sh_addr;
	Elf32_Off  sh_offset;
	Elf32_Word sh_size;
	Elf32_Word sh_link;
	Elf32_Word sh_info;
	Elf32_Word sh_addralign;
	Elf32_Word sh_entsize;
} Elf32_Shdr;

typedef struct __attribute__((packed)) {
	Elf32_Word st_name;
	Elf32_Addr st_value;
	Elf32_Word st_size;
	unsigned char st_info;
	unsigned char st_other;
	Elf32_Half st_shndx;
} Elf32_Sym;

typedef struct __attribute__((packed)) {
	Elf32_Off  r_offset;
	Elf32_Word r_info;
} Elf32_Rel;

#define R_386_INFO(sym, type) (((sym)<<8)+(type))

/* Fixed sizes for ELF32 (avoid sizeof/alignment issues on 64-bit host) */
#define ELF32_EHDR_SIZE 52
#define ELF32_SHDR_SIZE 40
#define ELF32_SYM_SIZE  16
#define ELF32_REL_SIZE  8

/* --- Buffers --- */
static void buf_append(unsigned char **buf, size_t *cap, size_t *len, const void *data, size_t n)
{
	while (*len + n > *cap) {
		*cap = *cap ? *cap * 2 : 4096;
		unsigned char *p = realloc(*buf, *cap);
		if (!p) { perror("realloc"); exit(1); }
		*buf = p;
	}
	memcpy(*buf + *len, data, n);
	*len += n;
}

static void buf_byte(unsigned char **b, size_t *cap, size_t *len, unsigned char x)
{ buf_append(b, cap, len, &x, 1); }

/* --- Compile .masm -> ASM string (unchanged logic) --- */
static const char print_string_routine[] =
	"\nprint_string:\n"
	".print_loop:\n"
	"    lodsb\n"
	"    test    %al, %al\n"
	"    jz      .print_done\n"
	"    mov     $0x0E, %ah\n"
	"    mov     $0x07, %bx\n"
	"    int     $0x10\n"
	"    jmp     .print_loop\n"
	".print_done:\n"
	"    ret\n";

static void append_str(char **buf, size_t *cap, size_t *len, const char *s, size_t n)
{
	while (*len + n + 1 > *cap) {
		*cap = *cap ? *cap * 2 : 4096;
		char *p = realloc(*buf, *cap);
		if (!p) { perror("realloc"); exit(1); }
		*buf = p;
	}
	memcpy(*buf + *len, s, n);
	*len += n;
	(*buf)[*len] = '\0';
}

static int match_print(const char *line, const char **start, const char **end)
{
	const char *p = line;
	while (*p && isspace((unsigned char)*p)) p++;
	if (strncmp(p, "print", 5) != 0) return 0;
	p += 5;
	while (*p && isspace((unsigned char)*p)) p++;
	if (*p != '"') return 0;
	p++;
	*start = p;
	while (*p && *p != '"') p++;
	if (*p != '"') return 0;
	*end = p;
	return 1;
}

static void escape_asciz(const char *s, const char *end, char *out, size_t *out_len)
{
	*out_len = 0;
	for (; s < end; s++) {
		if (*s == '\\' && s + 1 < end) {
			if (s[1] == 'r' || s[1] == 'n' || s[1] == 't') {
				out[(*out_len)++] = '\\';
				out[(*out_len)++] = s[1];
				s++;
			} else if (s[1] == '\\' || s[1] == '"') {
				out[(*out_len)++] = '\\';
				out[(*out_len)++] = s[1];
				s++;
			} else {
				out[(*out_len)++] = '\\';
				out[(*out_len)++] = '\\';
			}
		} else if (*s == '"') {
			out[(*out_len)++] = '\\';
			out[(*out_len)++] = '"';
		} else {
			out[(*out_len)++] = *s;
		}
	}
	out[*out_len] = '\0';
}

/* Parse string to raw bytes (for inline print); return byte count, stop at NUL or end */
static size_t string_to_bytes(const char *s, const char *end, unsigned char *buf, size_t buf_cap)
{
	size_t n = 0;
	for (; s < end && n < buf_cap; s++) {
		if (*s == '\\' && s + 1 < end) {
			s++;
			if (*s == 'n') buf[n++] = '\n';
			else if (*s == 'r') buf[n++] = '\r';
			else if (*s == 't') buf[n++] = '\t';
			else buf[n++] = (unsigned char)*s;
		} else {
			unsigned char c = (unsigned char)*s;
			if (c == 0) break;
			buf[n++] = c;
		}
	}
	return n;
}

static int is_org510(const char *line)
{
	const char *p = line;
	while (*p && isspace((unsigned char)*p)) p++;
	if (*p != '.' || strncmp(p, ".org", 4) != 0) return 0;
	p += 4;
	while (*p && isspace((unsigned char)*p)) p++;
	return (p[0]=='5'&&p[1]=='1'&&p[2]=='0'&&(!p[3]||isspace((unsigned char)p[3])));
}

static int compile_to_asm(const char *input_path, char **asm_out, size_t *asm_len)
{
	FILE *in = fopen(input_path, "r");
	if (!in) return -1;
	char *data_buf = NULL;
	size_t data_cap = 0, data_len = 0;
	int used_print = 0, seen_org510 = 0;
	char line[4096];
	*asm_out = NULL;
	*asm_len = 0;
	size_t out_cap = 4096;
	char *out = malloc(out_cap);
	if (!out) { fclose(in); return -1; }
	out[0] = '\0';
	size_t out_len = 0;

	while (fgets(line, sizeof line, in)) {
		const char *cs, *ce;
		if (match_print(line, &cs, &ce)) {
			used_print = 1;
			/* Inline print: mov $'X', %al; int $0x10 per char; set AH/BX before each int 0x10 */
			unsigned char bytes[256];
			size_t nbytes = string_to_bytes(cs, ce, bytes, sizeof bytes);
			for (size_t i = 0; i < nbytes; i++) {
				char buf[128];
				int n = snprintf(buf, sizeof buf,
					"    mov     $0x0E, %%ah\n"
					"    mov     $0, %%bx\n"
					"    mov     $0x%02X, %%al\n"
					"    int     $0x10\n", bytes[i]);
				append_str(&out, &out_cap, &out_len, buf, n);
			}
			continue;
		}
		if (!seen_org510 && is_org510(line)) {
			seen_org510 = 1;
			if (used_print && data_buf) {
				append_str(&out, &out_cap, &out_len, "\n", 1);
				append_str(&out, &out_cap, &out_len, data_buf, data_len);
				append_str(&out, &out_cap, &out_len, print_string_routine, sizeof print_string_routine - 1);
			}
		}
		append_str(&out, &out_cap, &out_len, line, strlen(line));
		if (out_len && out[out_len-1] != '\n') append_str(&out, &out_cap, &out_len, "\n", 1);
	}
	if (ferror(in)) { fclose(in); free(data_buf); free(out); return -1; }
	if (used_print && !seen_org510 && data_buf) {
		append_str(&out, &out_cap, &out_len, "\n", 1);
		append_str(&out, &out_cap, &out_len, data_buf, data_len);
		append_str(&out, &out_cap, &out_len, print_string_routine, sizeof print_string_routine - 1);
	}
	free(data_buf);
	fclose(in);
	*asm_out = out;
	*asm_len = out_len;
	return 0;
}

/* --- Symbol table (name -> index in .strtab, value = offset in section) --- */
#define MAX_SYMS 64
#define MAX_RELOC 128
static struct {
	char name[64];
	Elf32_Word value;
	Elf32_Half shndx;
	unsigned char type;
} syms[MAX_SYMS];
static int nsyms;
static Elf32_Rel relocs[MAX_RELOC];
static int nrelocs;

static int sym_add(const char *name, Elf32_Word value, Elf32_Half shndx, unsigned char type)
{
	if (nsyms >= MAX_SYMS) return -1;
	strncpy(syms[nsyms].name, name, 63);
	syms[nsyms].name[63] = '\0';
	syms[nsyms].value = value;
	syms[nsyms].shndx = shndx;
	syms[nsyms].type = type;
	return nsyms++;
}

static int sym_find(const char *name)
{
	int i;
	for (i = 0; i < nsyms; i++)
		if (strcmp(syms[i].name, name) == 0) return i;
	return -1;
}

static void reloc_add(Elf32_Off offset, int sym, int type)
{
	if (nrelocs >= MAX_RELOC) return;
	relocs[nrelocs].r_offset = offset;
	relocs[nrelocs].r_info = R_386_INFO(sym, type);
	nrelocs++;
}

/* Skip whitespace, return pointer past it */
static const char *skip_ws(const char *p) {
	while (*p && isspace((unsigned char)*p)) p++;
	return p;
}

/* Parse hex/dec number; *end advances. */
static unsigned long parse_num(const char *p, const char **end)
{
	*end = (char *)p;
	if (!*p) return 0;
	if (strncmp(p, "0x", 2) == 0) {
		unsigned long v = strtoul(p + 2, (char **)end, 16);
		return v;
	}
	return strtoul(p, (char **)end, 10);
}

/* Parse immediate: number (0xNN, decimal) or character constant 'D', '\n', etc. */
static unsigned long parse_imm(const char *p, const char **end)
{
	*end = (char *)p;
	if (!*p) return 0;
	if (p[0] == '\'' && p[1]) {
		unsigned long v;
		if (p[1] == '\\' && p[2]) {
			if (p[2] == 'n') v = '\n';
			else if (p[2] == 'r') v = '\r';
			else if (p[2] == 't') v = '\t';
			else v = (unsigned char)p[2];
			*end = (char *)(p + 4); /* past '\'' and closing '\'' */
		} else {
			v = (unsigned char)p[1];
			*end = (char *)(p + 3);
		}
		return v;
	}
	return parse_num(p, end);
}

/* Strip line: remove comment ( # or /* ), trim.
   *p_in_comment persists across lines for multi-line C comments. */
static void line_strip_ctx(char *line, int *p_in_comment)
{
	char *o = line;
	char *start = line;
	while (*line) {
		if (*p_in_comment) {
			if (line[0]=='*' && line[1]=='/') { line += 2; *p_in_comment = 0; continue; }
			line++;
			continue;
		}
		if (line[0]=='/' && line[1]=='*') { line += 2; *p_in_comment = 1; continue; }
		if (line[0]=='#') { *line = '\0'; break; }
		*o++ = *line++;
	}
	*o = '\0';
	while (o > start && isspace((unsigned char)o[-1])) *--o = '\0';
}
static void line_strip(char *line)
{
	int dummy = 0;
	line_strip_ctx(line, &dummy);
}

/* Copy one line from asm into line[], advance *pos. Return 0 if no more. */
static int next_line(const char *asm_buf, size_t asm_len, size_t *pos, char *line, size_t line_sz)
{
	if (*pos >= asm_len) return 0;
	const char *start = asm_buf + *pos;
	const char *p = start;
	while (p < asm_buf + asm_len && *p != '\n') p++;
	size_t n = (size_t)(p - start);
	if (n >= line_sz) n = line_sz - 1;
	memcpy(line, start, n);
	line[n] = '\0';
	*pos = p < asm_buf + asm_len ? (size_t)(p - asm_buf) + 1 : asm_len;
	return 1;
}

/* x86 16-bit encode: emit bytes into .text; return number of bytes, or -1 on unknown. */
static int encode_insn(const char *line, unsigned char *out, size_t text_off, size_t *reloc_sym, int *reloc_type)
{
	const char *p = skip_ws(line);
	*reloc_sym = -1;
	*reloc_type = -1;

	if (!*p) return 0;

	/* Label: already handled by caller */
	if (strchr(p, ':')) return -2;

	/* .section .text / .data */
	if (strncmp(p, ".section", 8) == 0) return -3;
	if (strncmp(p, ".global", 7) == 0 || strncmp(p, ".code16", 7) == 0) return 0;
	if (strncmp(p, ".asciz", 6) == 0) return -4; /* handled in data */
	if (strncmp(p, ".word", 5) == 0) return -5;
	if (strncmp(p, ".org", 4) == 0) return -6;

	/* cli, sti, hlt, lodsb, ret — match word so trailing space doesn't break */
	if (strncmp(p, "cli", 3) == 0 && (!p[3] || isspace((unsigned char)p[3]))) { out[0] = 0xFA; return 1; }
	if (strncmp(p, "sti", 3) == 0 && (!p[3] || isspace((unsigned char)p[3]))) { out[0] = 0xFB; return 1; }
	if (strncmp(p, "hlt", 3) == 0 && (!p[3] || isspace((unsigned char)p[3]))) { out[0] = 0xF4; return 1; }
	if (strncmp(p, "lodsb", 5) == 0 && (!p[5] || isspace((unsigned char)p[5]))) { out[0] = 0xAC; return 1; }
	if (strncmp(p, "ret", 3) == 0 && (!p[3] || isspace((unsigned char)p[3]))) { out[0] = 0xC3; return 1; }

	/* xor %ax, %ax */
	if (strstr(p, "xor") && strstr(p, "%ax") && strstr(p, "%ax")) {
		out[0] = 0x33; out[1] = 0xC0; return 2;
	}
	/* mov %ax, %ds / %es / %ss */
	if (strstr(p, "mov") && strstr(p, "%ax") && strstr(p, "%ds")) { out[0] = 0x8E; out[1] = 0xD8; return 2; }
	if (strstr(p, "mov") && strstr(p, "%ax") && strstr(p, "%es")) { out[0] = 0x8E; out[1] = 0xC0; return 2; }
	if (strstr(p, "mov") && strstr(p, "%ax") && strstr(p, "%ss")) { out[0] = 0x8E; out[1] = 0xD0; return 2; }
	/* mov $imm16, %sp */
	if (strstr(p, "mov") && strstr(p, "%sp")) {
		p = strchr(p, '$'); if (!p) return -1;
		unsigned long v = parse_num(p+1, &p);
		out[0] = 0xBC; out[1] = (unsigned char)(v & 0xFF); out[2] = (unsigned char)(v >> 8);
		return 3;
	}
	/* mov $imm8, %ah/%al/%ch/%cl/%dh (supports 'D', '\n', 0x0E, etc.) */
	if (strstr(p, "mov") && strstr(p, "$") && strstr(p, "%ah")) {
		p = strchr(p, '$'); unsigned long v = parse_imm(p+1, (const char**)&p);
		out[0] = 0xB4; out[1] = (unsigned char)(v & 0xFF); return 2;
	}
	if (strstr(p, "mov") && strstr(p, "$") && strstr(p, "%al")) {
		p = strchr(p, '$'); unsigned long v = parse_imm(p+1, (const char**)&p);
		out[0] = 0xB0; out[1] = (unsigned char)(v & 0xFF); return 2;
	}
	if (strstr(p, "mov") && strstr(p, "$") && strstr(p, "%ch")) {
		p = strchr(p, '$'); unsigned long v = parse_imm(p+1, (const char**)&p);
		out[0] = 0xB5; out[1] = (unsigned char)(v & 0xFF); return 2;
	}
	if (strstr(p, "mov") && strstr(p, "$") && strstr(p, "%cl")) {
		p = strchr(p, '$'); unsigned long v = parse_imm(p+1, (const char**)&p);
		out[0] = 0xB1; out[1] = (unsigned char)(v & 0xFF); return 2;
	}
	if (strstr(p, "mov") && strstr(p, "$") && strstr(p, "%dh")) {
		p = strchr(p, '$'); unsigned long v = parse_imm(p+1, (const char**)&p);
		out[0] = 0xB6; out[1] = (unsigned char)(v & 0xFF); return 2;
	}
	if (strstr(p, "mov") && strstr(p, "$") && strstr(p, "%bx") && !strstr(p, "%bh")) {
		p = strchr(p, '$'); unsigned long v = parse_num(p+1, &p);
		out[0] = 0xBB; out[1] = (unsigned char)(v & 0xFF); out[2] = (unsigned char)(v >> 8);
		return 3;
	}
	/* mov $imm16, %dx (for serial port) */
	if (strstr(p, "mov") && strstr(p, "$") && strstr(p, "%dx")) {
		p = strchr(p, '$'); unsigned long v = parse_num(p+1, &p);
		out[0] = 0xBA; out[1] = (unsigned char)(v & 0xFF); out[2] = (unsigned char)(v >> 8);
		return 3;
	}
	/* mov $sym, %si -> BE 00 00 + R_386_16 sym */
	if (strstr(p, "mov") && strstr(p, "$") && strstr(p, "%si")) {
		p = strchr(p, '$'); if (!p) return -1;
		const char *name = p + 1;
		while (*name && (isalnum((unsigned char)*name) || *name == '_' || *name == '.')) name++;
		char symname[64];
		size_t nl = (size_t)(name - (p+1));
		if (nl >= 63) nl = 62;
		memcpy(symname, p+1, nl); symname[nl] = '\0';
		int si = sym_find(symname);
		if (si < 0) si = sym_add(symname, 0, 0, STT_NOTYPE);
		out[0] = 0xBE; out[1] = 0; out[2] = 0;
		*reloc_sym = si; *reloc_type = R_386_16;
		return 3;
	}
	/* out %al, %dx (serial debug) */
	if ((strstr(p, "out") && strstr(p, "%al") && strstr(p, "%dx")) ||
	    (strstr(p, "outb") && strstr(p, "%dx"))) {
		out[0] = 0xEE; return 1;
	}
	/* int $0xNN */
	if (strncmp(p, "int", 3) == 0) {
		p = strchr(p, '$'); if (!p) return -1;
		unsigned long v = parse_num(p+1, &p);
		out[0] = 0xCD; out[1] = (unsigned char)(v & 0xFF);
		return 2;
	}
	/* push %ax / %bx */
	if (strcmp(p, "push    %ax") == 0 || strstr(p, "push") && strstr(p, "%ax")) { out[0] = 0x50; return 1; }
	if (strcmp(p, "push    %bx") == 0 || strstr(p, "push") && strstr(p, "%bx")) { out[0] = 0x53; return 1; }
	/* pop %bx / %ax */
	if (strstr(p, "pop") && strstr(p, "%bx")) { out[0] = 0x5B; return 1; }
	if (strstr(p, "pop") && strstr(p, "%ax")) { out[0] = 0x58; return 1; }
	/* test %al, %al */
	if (strstr(p, "test") && strstr(p, "%al")) { out[0] = 0x84; out[1] = 0xC0; return 2; }
	/* mov $0x0E, %ah ... etc already above */
	/* mov $0x07, %bx - special: 16-bit imm */
	if (strstr(p, "mov") && strstr(p, "$0x07") && strstr(p, "%bx")) {
		out[0] = 0xBB; out[1] = 0x07; out[2] = 0x00; return 3;
	}
	/* jc sym -> 0F 82 rw (jc rel16, so relocation always fits) */
	if (strncmp(p, "jc", 2) == 0) {
		p = skip_ws(p + 2);
		char symname[64]; int i = 0;
		while (*p && (isalnum((unsigned char)*p) || *p == '_' || *p == '.') && i < 63) symname[i++] = *p++;
		symname[i] = '\0';
		int si = sym_find(symname);
		if (si < 0) si = sym_add(symname, 0, 0, STT_NOTYPE);
		out[0] = 0x0F; out[1] = 0x82; out[2] = 0xFE; out[3] = 0xFF;
		*reloc_sym = si; *reloc_type = R_386_PC16;
		return 4;
	}
	/* jz sym -> 0F 84 rw (jz rel16) */
	if (strncmp(p, "jz", 2) == 0) {
		p = skip_ws(p + 2);
		char symname[64]; int i = 0;
		while (*p && (isalnum((unsigned char)*p) || *p == '_' || *p == '.') && i < 63) symname[i++] = *p++;
		symname[i] = '\0';
		int si = sym_find(symname);
		if (si < 0) si = sym_add(symname, 0, 0, STT_NOTYPE);
		out[0] = 0x0F; out[1] = 0x84; out[2] = 0xFE; out[3] = 0xFF;
		*reloc_sym = si; *reloc_type = R_386_PC16;
		return 4;
	}
	/* call sym */
	if (strncmp(p, "call", 4) == 0) {
		p = skip_ws(p + 4);
		char symname[64]; int i = 0;
		while (*p && (isalnum((unsigned char)*p) || *p == '_' || *p == '.') && i < 63) symname[i++] = *p++;
		symname[i] = '\0';
		int si = sym_find(symname);
		if (si < 0) si = sym_add(symname, 0, 0, STT_NOTYPE);
		out[0] = 0xE8; out[1] = 0xFE; out[2] = 0xFF;
		*reloc_sym = si; *reloc_type = R_386_PC16;
		return 3;
	}
	/* jmp sym or jmp 0x7E00 (absolute: use synthetic symbol for linker) */
	if (strncmp(p, "jmp", 3) == 0) {
		p = skip_ws(p + 3);
		if (isdigit((unsigned char)*p) || (p[0]=='0'&&p[1]=='x')) {
			unsigned long target = parse_num(p, &p);
			char absname[32];
			int n = snprintf(absname, sizeof absname, "__abs_%lx", target);
			if (n >= (int)sizeof absname) n = (int)sizeof absname - 1;
			int si = sym_find(absname);
			if (si < 0) si = sym_add(absname, (Elf32_Word)target, SHN_ABS, STT_NOTYPE);
			out[0] = 0xE9; out[1] = 0xFE; out[2] = 0xFF;
			*reloc_sym = si; *reloc_type = R_386_PC16;
			return 3;
		}
		char symname[64]; int i = 0;
		while (*p && (isalnum((unsigned char)*p) || *p == '_' || *p == '.') && i < 63) symname[i++] = *p++;
		symname[i] = '\0';
		int si = sym_find(symname);
		if (si < 0) si = sym_add(symname, 0, 0, STT_NOTYPE);
		out[0] = 0xE9; out[1] = 0xFE; out[2] = 0xFF;
		*reloc_sym = si; *reloc_type = R_386_PC16;
		return 3;
	}
	return -1;
}

/* Assemble ASM buffer -> .text and .data; build symtab and relocs. Then write ELF. */
static int assemble_to_elf(const char *asm_buf, size_t asm_len, const char *out_path)
{
	nsyms = 0;
	nrelocs = 0;
	unsigned char *text = NULL;
	size_t text_cap = 0, text_len = 0;
	unsigned char *data = NULL;
	size_t data_cap = 0, data_len = 0;
	int in_data = 0;
	size_t pos = 0;
	char line[512];
	unsigned char enc[8];
	size_t reloc_sym;
	int reloc_type;
	int pending_sym_idx = -1;  /* code label: set value when next instruction is emitted */

	/* First pass: register labels in file order so symbol index matches second-pass label order */
	{
		int comment_state = 0;
		size_t scan_pos = 0;
		while (next_line(asm_buf, asm_len, &scan_pos, line, sizeof line)) {
			line_strip_ctx(line, &comment_state);
			const char *q = skip_ws(line);
			if (!*q) continue;
			char *col = strchr(line, ':');
			if (!col) continue;
			*col = '\0';
			const char *lbl = skip_ws(line);
			if (!*lbl) continue;
			if (strncmp(lbl, "ret", 3) == 0 && (!lbl[3] || isspace((unsigned char)lbl[3]))) continue;
			if (sym_find(lbl) < 0) sym_add(lbl, 0, 0, STT_NOTYPE);
		}
	}

	/* Second pass: emit code, set symbol values, add relocs */
	int comment_state2 = 0;
	while (next_line(asm_buf, asm_len, &pos, line, sizeof line)) {
		line_strip_ctx(line, &comment_state2);
		const char *p = skip_ws(line);
		if (!*p) continue;

		/* Label */
		char *colon = strchr(line, ':');
		if (colon) {
			*colon = '\0';
			const char *lbl = skip_ws(line);
			if (*lbl) {
				/* "ret:" often a typo — emit ret instruction (C3), no label/NOPs */
				if (strncmp(lbl, "ret", 3) == 0 && (!lbl[3] || isspace((unsigned char)lbl[3]))) {
					buf_byte(&text, &text_cap, &text_len, 0xC3);
					continue;
				}
				/* __magic_msg_* + .asciz go into .text so boot sector (512 bytes) contains everything */
				if (strncmp(lbl, "__magic_msg_", 12) == 0) in_data = 1;
				else if (strcmp(lbl, "print_string") == 0 || strncmp(lbl, ".print", 6) == 0) in_data = 0;
				else if (strcmp(lbl, "_start") != 0 && strcmp(lbl, "halt") != 0) {
					/* Code label (not _start/halt): pad 4 NOPs so call rel16 lands on first insn */
					buf_byte(&text, &text_cap, &text_len, 0x90);
					buf_byte(&text, &text_cap, &text_len, 0x90);
					buf_byte(&text, &text_cap, &text_len, 0x90);
					buf_byte(&text, &text_cap, &text_len, 0x90);
				}
				int shndx = 1;  /* always .text */
				size_t val = text_len;
				int idx = sym_find(lbl);
				if (idx < 0) idx = sym_add(lbl, 0, (Elf32_Half)shndx, STT_NOTYPE);
				else syms[idx].shndx = (Elf32_Half)shndx;
				/* Code labels: set value when next instruction is emitted (pending_sym_idx) so call lands on first insn */
				if (strcmp(lbl, "_start") != 0 && strcmp(lbl, "halt") != 0 &&
				    strncmp(lbl, "__magic_msg_", 12) != 0) {
					pending_sym_idx = idx;
				} else {
					syms[idx].value = (Elf32_Word)val;
					pending_sym_idx = -1;
				}
			}
			continue;
		}

		if (strncmp(p, ".section", 8) == 0) {
			if (strstr(p, ".data")) in_data = 1;
			else in_data = 0;
			if (pending_sym_idx >= 0) { syms[pending_sym_idx].value = (Elf32_Word)text_len; pending_sym_idx = -1; }
			continue;
		}
		if (strncmp(p, ".asciz", 6) == 0) {
			if (pending_sym_idx >= 0) { syms[pending_sym_idx].value = (Elf32_Word)text_len; pending_sym_idx = -1; }
			p = strchr(p, '"');
			if (!p) continue;
			p++;
			/* Append to .text so strings are in same 512-byte boot sector */
			while (*p && *p != '"') {
				if (*p == '\\' && p[1]) {
					p++;
					if (*p == 'n') buf_byte(&text, &text_cap, &text_len, '\n');
					else if (*p == 'r') buf_byte(&text, &text_cap, &text_len, '\r');
					else if (*p == 't') buf_byte(&text, &text_cap, &text_len, '\t');
					else buf_byte(&text, &text_cap, &text_len, *p);
					p++;
				} else {
					buf_byte(&text, &text_cap, &text_len, (unsigned char)*p);
					p++;
				}
			}
			buf_byte(&text, &text_cap, &text_len, 0);
			continue;
		}
		if (strncmp(p, ".org", 4) == 0) {
			if (pending_sym_idx >= 0) { syms[pending_sym_idx].value = (Elf32_Word)text_len; pending_sym_idx = -1; }
			unsigned long to = 510;
			const char *q = p + 4;
			while (*q && !isdigit((unsigned char)*q)) q++;
			if (*q) to = parse_num(q, &q);
			while (text_len < to) buf_byte(&text, &text_cap, &text_len, 0);
			continue;
		}
		if (strncmp(p, ".word", 5) == 0) {
			if (pending_sym_idx >= 0) { syms[pending_sym_idx].value = (Elf32_Word)text_len; pending_sym_idx = -1; }
			p += 5; p = skip_ws(p);
			unsigned long v = parse_num(p, &p);
			buf_byte(&text, &text_cap, &text_len, (unsigned char)(v & 0xFF));
			buf_byte(&text, &text_cap, &text_len, (unsigned char)(v >> 8));
			continue;
		}
		if (strncmp(p, ".global", 7) == 0 || strncmp(p, ".code16", 7) == 0) continue;

		/* Encode instruction */
		int n = encode_insn(line, enc, text_len, (size_t*)&reloc_sym, &reloc_type);
		if (n == -2 || n == -3 || n == -4 || n == -5 || n == -6) continue;
		if (n > 0) {
			if (pending_sym_idx >= 0) {
				syms[pending_sym_idx].value = (Elf32_Word)text_len;
				pending_sym_idx = -1;
			}
			buf_append(&text, &text_cap, &text_len, enc, n);
			if (reloc_type >= 0 && nrelocs < MAX_RELOC) {
				/* r_offset = first byte to patch: for PC8 the 1 byte at end, for 16-bit the 2 bytes at end */
				relocs[nrelocs].r_offset = (Elf32_Off)text_len - (reloc_type == R_386_PC8 ? 1 : 2);
				relocs[nrelocs].r_info = R_386_INFO((int)reloc_sym, reloc_type);
				nrelocs++;
			}
		}
	}

	/* Symbol types: STT_FUNC for code labels, STT_OBJECT for __magic_msg_* (strings in .text) */
	{
		int i;
		for (i = 0; i < nsyms; i++) {
			if (strncmp(syms[i].name, "__magic_msg_", 12) == 0)
				syms[i].type = STT_OBJECT;
			else if (strcmp(syms[i].name, "_start") == 0 || strcmp(syms[i].name, "print_string") == 0 ||
			    strncmp(syms[i].name, ".print", 6) == 0 || strcmp(syms[i].name, "disk_error") == 0 || strcmp(syms[i].name, "halt") == 0)
				syms[i].type = STT_FUNC;
		}
	}

	/* Build .strtab and name offsets (do not overwrite syms[].value) */
	size_t strtab_len = 1;
	int i;
	for (i = 0; i < nsyms; i++)
		strtab_len += strlen(syms[i].name) + 1;
	unsigned char *strtab = malloc(strtab_len);
	if (!strtab) { free(text); free(data); return -1; }
	Elf32_Word str_off[MAX_SYMS];
	size_t so = 1;
	strtab[0] = '\0';
	for (i = 0; i < nsyms; i++) {
		str_off[i] = (Elf32_Word)so;
		size_t sl = strlen(syms[i].name) + 1;
		memcpy(strtab + so, syms[i].name, sl);
		so += sl;
	}

	/* Section name string table (strlen would be 0 due to leading \0) */
	static consst char shstr[] = "\0.text\0.data\0.shstrtab\0.symtab\0.strtab\0.rel.text";
	size_t shstr_len = sizeof(shstr); /* include trailing \0 */

	/* File layout: ehdr, .text, .data, .shstrtab, .symtab, .strtab, .rel.text, section headers */
	Elf32_Off off = ELF32_EHDR_SIZE;
	Elf32_Off off_text = off;
	off += text_len;
	Elf32_Off off_data = off;
	off += data_len;
	Elf32_Off off_shstrtab = off;
	off += shstr_len;
	Elf32_Off off_symtab = off;
	off += (nsyms + 1) * ELF32_SYM_SIZE;
	Elf32_Off off_strtab = off;
	off += strtab_len;
	Elf32_Off off_rel = off;
	off += nrelocs * ELF32_REL_SIZE;
	Elf32_Off off_sh = off;

	FILE *f = fopen(out_path, "wb");
	if (!f) { free(text); free(data); free(strtab); return -1; }

	/* Apply relocations so objcopy -j .text -O binary gets runnable boot sector (base 0x7C00) */
	unsigned char *text_out = malloc(text_len);
	if (!text_out) { fclose(f); free(text); free(data); free(strtab); return -1; }
	memcpy(text_out, text, text_len);
	for (i = 0; i < nrelocs; i++) {
		int sym_i = (relocs[i].r_info >> 8) & 0xFF;
		int type = relocs[i].r_info & 0xFF;
		size_t o = relocs[i].r_offset;
		uint32_t S = (uint32_t)syms[sym_i].value;
		uint32_t base = 0x7C00;
		if (type == R_386_16) {
			uint16_t v = (uint16_t)((base + S) & 0xFFFF);
			text_out[o]   = (unsigned char)(v & 0xFF);
			text_out[o+1] = (unsigned char)(v >> 8);
		} else if (type == R_386_PC16) {
			/* rel16: (next_insn + disp) == target. E8/jmp: insn at o-2, len 3, next_insn at o+2 */
			int32_t delta = (int32_t)(base + S) - (int32_t)(base + o + 2);
			uint16_t v = (uint16_t)(delta & 0xFFFF);
			text_out[o]   = (unsigned char)(v & 0xFF);
			text_out[o+1] = (unsigned char)(v >> 8);
		}
	}

	/* Write ELF header with fixed layout (no struct padding) */
	{
		unsigned char ehdr_buf[ELF32_EHDR_SIZE];
		memset(ehdr_buf, 0, sizeof ehdr_buf);
		ehdr_buf[0] = 0x7F;
		ehdr_buf[1] = 'E';
		ehdr_buf[2] = 'L';
		ehdr_buf[3] = 'F';
		ehdr_buf[4] = 1;  /* ELFCLASS32 */
		ehdr_buf[5] = 1;  /* ELFDATA2LSB */
		ehdr_buf[6] = 1;  /* EV_CURRENT */
		*(uint16_t *)(ehdr_buf + 16) = ET_REL;
		*(uint16_t *)(ehdr_buf + 18) = EM_386;
		*(uint32_t *)(ehdr_buf + 20) = 1;
		*(uint32_t *)(ehdr_buf + 32) = off_sh;
		*(uint16_t *)(ehdr_buf + 40) = ELF32_EHDR_SIZE;
		*(uint16_t *)(ehdr_buf + 42) = 0;   /* e_phentsize */
		*(uint16_t *)(ehdr_buf + 44) = 0;   /* e_phnum */
		*(uint16_t *)(ehdr_buf + 46) = ELF32_SHDR_SIZE;
		*(uint16_t *)(ehdr_buf + 48) = 7;   /* e_shnum */
		*(uint16_t *)(ehdr_buf + 50) = 2;   /* e_shstrndx */
		fwrite(ehdr_buf, 1, ELF32_EHDR_SIZE, f);
	}

	/* Write ORIGINAL text (with zero placeholders) — ld will apply relocs from the reloc table.
	   text_out has pre-resolved addresses but is NOT written here to avoid double-application by ld. */
	fwrite(text, 1, text_len, f);
	free(text_out);
	if (data_len > 0 && data) fwrite(data, 1, data_len, f);
	fwrite(shstr, 1, shstr_len, f);

	/* Symbol table: null sym + entries, 16 bytes each */
	{
		unsigned char sym_buf[ELF32_SYM_SIZE];
		memset(sym_buf, 0, ELF32_SYM_SIZE);
		fwrite(sym_buf, 1, ELF32_SYM_SIZE, f);
	}
	for (i = 0; i < nsyms; i++) {
		unsigned char sym_buf[ELF32_SYM_SIZE];
		memset(sym_buf, 0, ELF32_SYM_SIZE);
		*(uint32_t *)(sym_buf + 0) = str_off[i];
		*(uint32_t *)(sym_buf + 4) = syms[i].value;
		sym_buf[12] = ELF32_ST_INFO(STB_GLOBAL, syms[i].type);
		*(uint16_t *)(sym_buf + 14) = syms[i].shndx;
		fwrite(sym_buf, 1, ELF32_SYM_SIZE, f);
	}
	fwrite(strtab, 1, strtab_len, f);
	for (i = 0; i < nrelocs; i++) {
		unsigned char rel_buf[ELF32_REL_SIZE];
		*(uint32_t *)(rel_buf + 0) = relocs[i].r_offset;
		/* ELF symtab has null entry at index 0; our syms[] start at ELF index 1 */
		int rs = (relocs[i].r_info >> 8) & 0xFF;
		int rt = relocs[i].r_info & 0xFF;
		*(uint32_t *)(rel_buf + 4) = (uint32_t)(((rs + 1) << 8) | rt);
		fwrite(rel_buf, 1, ELF32_REL_SIZE, f);
	}

	/* Section headers, 40 bytes each */
	unsigned char sh_buf[7][ELF32_SHDR_SIZE];
	memset(sh_buf, 0, sizeof sh_buf);
	*(uint32_t *)(sh_buf[0] + 0) = 0;
	*(uint32_t *)(sh_buf[1] + 0) = 1;
	*(uint32_t *)(sh_buf[1] + 4) = SHT_PROGBITS;
	*(uint32_t *)(sh_buf[1] + 8) = SHF_ALLOC | SHF_EXECINSTR;
	*(uint32_t *)(sh_buf[1] + 16) = off_text;
	*(uint32_t *)(sh_buf[1] + 20) = text_len;
	*(uint32_t *)(sh_buf[1] + 32) = 1;
	*(uint32_t *)(sh_buf[2] + 0) = 13;
	*(uint32_t *)(sh_buf[2] + 4) = SHT_STRTAB;
	*(uint32_t *)(sh_buf[2] + 16) = off_shstrtab;
	*(uint32_t *)(sh_buf[2] + 20) = shstr_len;
	*(uint32_t *)(sh_buf[3] + 0) = 7;
	*(uint32_t *)(sh_buf[3] + 4) = SHT_PROGBITS;
	*(uint32_t *)(sh_buf[3] + 8) = SHF_ALLOC;
	*(uint32_t *)(sh_buf[3] + 16) = off_data;
	*(uint32_t *)(sh_buf[3] + 20) = data_len;
	*(uint32_t *)(sh_buf[3] + 32) = 1;
	*(uint32_t *)(sh_buf[4] + 0) = 22;
	*(uint32_t *)(sh_buf[4] + 4) = SHT_SYMTAB;
	*(uint32_t *)(sh_buf[4] + 16) = off_symtab;
	*(uint32_t *)(sh_buf[4] + 20) = (nsyms + 1) * ELF32_SYM_SIZE;
	*(uint32_t *)(sh_buf[4] + 24) = 5;
	*(uint32_t *)(sh_buf[4] + 28) = 1;
	*(uint32_t *)(sh_buf[4] + 32) = 4;
	*(uint32_t *)(sh_buf[4] + 36) = ELF32_SYM_SIZE;
	*(uint32_t *)(sh_buf[5] + 0) = 30;
	*(uint32_t *)(sh_buf[5] + 4) = SHT_STRTAB;
	*(uint32_t *)(sh_buf[5] + 16) = off_strtab;
	*(uint32_t *)(sh_buf[5] + 20) = strtab_len;
	*(uint32_t *)(sh_buf[6] + 0) = 37;
	*(uint32_t *)(sh_buf[6] + 4) = SHT_REL;
	*(uint32_t *)(sh_buf[6] + 16) = off_rel;
	*(uint32_t *)(sh_buf[6] + 20) = nrelocs * ELF32_REL_SIZE;
	*(uint32_t *)(sh_buf[6] + 24) = 4;
	*(uint32_t *)(sh_buf[6] + 28) = 1;
	*(uint32_t *)(sh_buf[6] + 32) = 4;
	*(uint32_t *)(sh_buf[6] + 36) = ELF32_REL_SIZE;
	fwrite(sh_buf, 1, sizeof sh_buf, f);

	fclose(f);
	free(text);
	free(data);
	free(strtab);
	return 0;
}

static int wants_elf(const char *path)
{
	size_t n = strlen(path);
	if (n >= 2 && path[n-1] == 'o' && path[n-2] == '.') return 1;
	if (n >= 4 && path[n-1] == 'f' && path[n-2] == 'l' && path[n-3] == 'e' && path[n-4] == '.') return 1;
	return 0;
}

int main(int argc, char **argv)
{
	if (argc != 3) {
		fprintf(stderr, "usage: magic-asm <input.masm> <output.S|output.o|output.elf>\n");
		return 1;
	}
	const char *input_path = argv[1];
	const char *output_path = argv[2];

	if (wants_elf(output_path)) {
		char *asm_buf = NULL;
		size_t asm_len = 0;
		if (compile_to_asm(input_path, &asm_buf, &asm_len) != 0) {
			fprintf(stderr, "magic-asm: compile failed\n");
			return 1;
		}
		if (assemble_to_elf(asm_buf, asm_len, output_path) != 0) {
			free(asm_buf);
			fprintf(stderr, "magic-asm: ELF emit failed\n");
			return 1;
		}
		free(asm_buf);
		return 0;
	}

	FILE *out = fopen(output_path, "w");
	if (!out) { perror("magic-asm"); return 1; }
	char *asm_buf = NULL;
	size_t asm_len = 0;
	if (compile_to_asm(input_path, &asm_buf, &asm_len) != 0) {
		fclose(out);
		return 1;
	}
	fwrite(asm_buf, 1, asm_len, out);
	fclose(out);
	free(asm_buf);
	return 0;
}
