#pragma once

// Gecko/Action Replay-style "basic write" cheat codes, read from Config.toml's [cheats] codes
// list. Supported types (ba-relative only - see below for why): 00/01 (8-bit write & fill),
// 02/03 (16-bit write & fill), 04/05 (32-bit write), 06/07 (string write - a raw byte sequence
// spanning however many following lines its own byte count needs), 08/09 (write & fill with an
// address AND value increment per step, two lines). This mirrors
// translator/src/Translator.Core/Parsing/Dol/DolCodePatcher.cs's ParseLines exactly - the two
// implementations must stay in sync, since a code that means one thing at translate time (for a
// code_patches.local.txt entry targeting .text) must mean the same thing here (for a
// Config.toml entry targeting data/RAM). See that file for the full type-family rationale.
//
// Applied every VBlank (see vi.cpp's AdvanceRetrace), not once at boot: that matches what a real
// Gecko codehandler does on console - it walks the active code list every frame, which is why a
// cheat "sticks" against game logic that keeps recomputing the same variable. A one-shot boot poke
// (see system_bridge.cpp's low-memory seeding) would just get overwritten on the next frame for
// most cheats, though it is the right tool for a value the game only reads once at startup.
//
// The *list itself* is also periodically re-read from Config.toml (see ApplyAll's poll), not just
// the writes it produces: without that, a code checked/unchecked in the Cheats UI while the game
// is already running would need a full relaunch to take effect, since the parsed list used to be
// computed exactly once at boot.
//
// 'po' (pointer-relative) code types (10-19, including the 06/07 string write's own po sibling
// 16/17) are not supported: they depend on a prior code
// having set a shared pointer register, and nothing here tracks that state across codes. Code
// hooks (type C2, "insert ASM at an address") are not supported either, and neither is a
// 00/01/02/03/04/05/08/09 code whose *target* lands in .text rather than a data/bss section -
// both need PowerPC instructions injected at a specific point in the *translated* code, which
// this runtime memory poke architecturally cannot do (see DolCodePatcher.cs's comment for why -
// this recomp never re-fetches-and-decodes guest instruction bytes at runtime the way an
// interpreter or JIT would). Anything else Gecko defines (conditionals, ASM, registers, flow
// control, ...) is likewise unsupported. Every skipped/no-op case is logged once so a code that
// "does nothing" is at least explained.

#include <cctype>
#include <cstdint>
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <string>
#include <vector>

#include "memory.h"
#include "runtime_config.h"

namespace CheatCodes {

struct SimpleWrite {
    uint32_t address;
    uint32_t value;
    uint8_t widthBytes;
    std::string source;  // The originating line(s), for diagnostics.
};

inline bool ParseHexWord(const std::string& text, uint32_t& outValue) {
    if (text.empty() || text.size() > 8) {
        return false;
    }
    for (const char ch : text) {
        if (!std::isxdigit(static_cast<unsigned char>(ch))) {
            return false;
        }
    }
    outValue = static_cast<uint32_t>(std::strtoul(text.c_str(), nullptr, 16));
    return true;
}

// True/false pair mirrors std::optional-of-pair without needing <utility> gymnastics for two
// uint32_t out-values; kept private to this header.
inline bool ParseWordPair(const std::string& line, uint32_t& addressWord, uint32_t& valueWord) {
    const size_t space = line.find_first_of(" \t");
    if (space == std::string::npos) {
        return false;
    }
    const std::string first = line.substr(0, space);
    const size_t valueBegin = line.find_first_not_of(" \t", space);
    if (valueBegin == std::string::npos) {
        return false;
    }
    const std::string second = line.substr(valueBegin, 8);
    return ParseHexWord(first, addressWord) && ParseHexWord(second, valueWord);
}

// How many additional lines follow this line's own "AAAAAAAA VVVVVVVV" pair (1 for 08/09's
// "TNNNZZZZ VVVVVVVV" control line, ceil(byteCount/8) for a 06/07 string write, 0 otherwise).
// Blank/comment/unparseable lines never need one as far as this check is concerned.
inline int AdditionalLinesRequired(const std::string& rawLine) {
    std::string line = rawLine;
    const size_t begin = line.find_first_not_of(" \t\r\n");
    if (begin == std::string::npos) {
        return 0;
    }
    line = line.substr(begin);
    if (line[0] == '#' || line[0] == ';') {
        return 0;
    }
    uint32_t addressWord = 0;
    uint32_t valueWord = 0;
    if (!ParseWordPair(line, addressWord, valueWord)) {
        return 0;
    }
    const uint8_t baseCodeType = static_cast<uint8_t>((addressWord >> 24) & ~0x01u);
    if (baseCodeType == 0x08 || baseCodeType == 0x18) {
        return 1;
    }
    if (baseCodeType == 0x06 || baseCodeType == 0x16) {
        return static_cast<int>((valueWord + 7) / 8);
    }
    return 0;
}

// 0x80000000 + the 24-bit offset, +0x01000000 more if codeType is the "high address" sibling of
// its base type (00->01, 02->03, 04->05, 08->09). outBaseCodeType receives the base type (00/02/
// 04/08) so the caller only switches on four values instead of eight.
inline uint32_t ResolveBaAddress(uint32_t addressWord, uint8_t& outBaseCodeType) {
    const uint8_t codeType = static_cast<uint8_t>(addressWord >> 24);
    const bool isHigh = (codeType & 0x01) != 0;
    outBaseCodeType = static_cast<uint8_t>(codeType & ~0x01u);
    uint32_t offset = addressWord & 0x00FFFFFFu;
    if (isHigh) {
        offset += 0x01000000u;
    }
    return 0x80000000u + offset;
}

inline void AddFill(std::vector<SimpleWrite>& result, uint32_t address, uint32_t value,
                     uint8_t widthBytes, uint32_t fillCount, const std::string& source) {
    for (uint32_t i = 0; i < fillCount; ++i) {
        result.push_back(SimpleWrite{address + i * widthBytes, value, widthBytes, source});
    }
}

// Appends a 32-bit word's four bytes MSB-first (d1d2d3d4, matching how the hex word is written) -
// the packing a type 06/07 string write's data lines use.
inline void AppendBytes(std::vector<uint8_t>& bytes, uint32_t word) {
    bytes.push_back(static_cast<uint8_t>(word >> 24));
    bytes.push_back(static_cast<uint8_t>(word >> 16));
    bytes.push_back(static_cast<uint8_t>(word >> 8));
    bytes.push_back(static_cast<uint8_t>(word));
}

// Parses a whole block of lines (comments/blank lines skipped), consuming a second line for any
// code type that needs one. Unlike the C# translator side (which throws - a bad code_patches
// entry should fail the build loudly), a bad runtime cheat entry is just skipped with a logged
// reason: this runs at game boot, and refusing to start the game over a malformed Config.toml
// line the player may not have even written themselves would be far worse than ignoring it.
inline std::vector<SimpleWrite> ParseAllCodes(const std::vector<std::string>& rawLines) {
    std::vector<SimpleWrite> result;
    size_t index = 0;
    while (index < rawLines.size()) {
        std::string line = rawLines[index];
        ++index;
        const size_t begin = line.find_first_not_of(" \t\r\n");
        if (begin == std::string::npos) {
            continue;
        }
        const size_t end = line.find_last_not_of(" \t\r\n");
        line = line.substr(begin, end - begin + 1);
        if (line.empty() || line[0] == '#' || line[0] == ';') {
            continue;
        }

        uint32_t addressWord = 0;
        uint32_t valueWord = 0;
        if (!ParseWordPair(line, addressWord, valueWord)) {
            std::cerr << "[cheats] Skipping \"" << line << "\": expected \"AAAAAAAA VVVVVVVV\"" << std::endl;
            continue;
        }

        const uint8_t codeType = static_cast<uint8_t>(addressWord >> 24);
        if (codeType == 0x10 || codeType == 0x11 || codeType == 0x12 || codeType == 0x13 ||
            codeType == 0x14 || codeType == 0x15 || codeType == 0x16 || codeType == 0x17 ||
            codeType == 0x18 || codeType == 0x19) {
            std::cerr << "[cheats] Skipping \"" << line << "\": 'po' (pointer-relative) code types "
                      << "are not supported" << std::endl;
            continue;
        }

        uint8_t baseCodeType = 0;
        const uint32_t address = ResolveBaAddress(addressWord, baseCodeType);

        if (baseCodeType == 0x00) {
            AddFill(result, address, valueWord & 0xFFu, 1, (valueWord >> 16) & 0xFFFFu, line);
        } else if (baseCodeType == 0x02) {
            AddFill(result, address, valueWord & 0xFFFFu, 2, (valueWord >> 16) & 0xFFFFu, line);
        } else if (baseCodeType == 0x04) {
            result.push_back(SimpleWrite{address, valueWord, 4, line});
        } else if (baseCodeType == 0x06) {
            const uint32_t byteCount = valueWord;
            if (byteCount == 0) {
                continue;
            }
            const uint32_t lineCount = (byteCount + 7) / 8;
            std::vector<uint8_t> bytes;
            bytes.reserve(byteCount + 8);
            bool stringWriteOk = true;
            for (uint32_t i = 0; i < lineCount; ++i) {
                if (index >= rawLines.size()) {
                    std::cerr << "[cheats] Skipping \"" << line << "\": string write (type 06/07) of "
                              << byteCount << " byte(s) needs " << lineCount << " more line(s) of raw "
                              << "bytes; only " << i << " were found" << std::endl;
                    stringWriteOk = false;
                    break;
                }
                std::string dataLine = rawLines[index];
                ++index;
                uint32_t word1 = 0;
                uint32_t word2 = 0;
                if (!ParseWordPair(dataLine, word1, word2)) {
                    std::cerr << "[cheats] Skipping \"" << line << " / " << dataLine << "\": string "
                              << "write data line is not \"AAAAAAAA VVVVVVVV\"" << std::endl;
                    stringWriteOk = false;
                    break;
                }
                AppendBytes(bytes, word1);
                AppendBytes(bytes, word2);
            }
            if (!stringWriteOk) {
                continue;
            }
            for (uint32_t i = 0; i < byteCount; ++i) {
                result.push_back(SimpleWrite{address + i, bytes[i], 1, line});
            }
        } else if (baseCodeType == 0x08) {
            if (index >= rawLines.size()) {
                std::cerr << "[cheats] Skipping \"" << line << "\": type 08/09 needs a second "
                          << "\"TNNNZZZZ VVVVVVVV\" line that is missing" << std::endl;
                continue;
            }
            std::string secondLine = rawLines[index];
            ++index;
            const std::string combinedSource = line + " / " + secondLine;
            uint32_t control = 0;
            uint32_t valueIncrement = 0;
            if (!ParseWordPair(secondLine, control, valueIncrement)) {
                std::cerr << "[cheats] Skipping \"" << combinedSource << "\": second line is not "
                          << "\"TNNNZZZZ VVVVVVVV\"" << std::endl;
                continue;
            }
            const uint32_t t = (control >> 28) & 0xFu;
            uint8_t widthBytes = 0;
            if (t == 0) {
                widthBytes = 1;
            } else if (t == 1) {
                widthBytes = 2;
            } else if (t == 2) {
                widthBytes = 4;
            } else {
                std::cerr << "[cheats] Skipping \"" << combinedSource << "\": value size T=0x"
                          << std::hex << t << std::dec << " is not 0/1/2" << std::endl;
                continue;
            }
            const uint32_t additionalWrites = (control >> 16) & 0xFFFu;
            const uint32_t addressIncrement = control & 0xFFFFu;
            for (uint32_t step = 0; step <= additionalWrites; ++step) {
                const uint32_t stepAddress = address + step * addressIncrement;
                const uint32_t stepValue = valueWord + step * valueIncrement;
                result.push_back(SimpleWrite{stepAddress, stepValue, widthBytes, combinedSource});
            }
        } else {
            std::cerr << "[cheats] Skipping \"" << line << "\": code type 0x" << std::hex
                      << std::uppercase << static_cast<unsigned int>(codeType) << std::dec
                      << std::nouppercase << " is not implemented (only the basic-write family "
                      << "00/01, 02/03, 04/05, 06/07, 08/09 is)" << std::endl;
        }
    }
    return result;
}

// Not `static const`: reparsed whenever a poll (see ApplyAll) finds Config.toml's [cheats] codes
// actually changed, so a code toggled on/off in the Cheats UI while the game is already running
// takes effect without a restart - originally this was computed exactly once at boot and never
// revisited, which is what made a live toggle silently do nothing until the next launch.
inline std::vector<SimpleWrite>& ParsedCodes() {
    static std::vector<SimpleWrite> codes = [] {
        auto parsed = ParseAllCodes(RuntimeConfigFile::Get().cheatCodes);
        if (!parsed.empty()) {
            std::cout << "[cheats] " << parsed.size() << " simple RAM-write(s) active" << std::endl;
        }
        return parsed;
    }();
    return codes;
}

// Re-reads Config.toml's [cheats] codes (see RuntimeConfigFile::ReloadCheatCodes) and, only if the
// raw line list actually changed since last poll, reparses and swaps it into ParsedCodes(). Kept
// separate from ParsedCodes() itself so the common case (nothing changed) is just a small file
// read and a vector comparison, not a full TOML reparse every poll.
inline void PollForChanges() {
    static std::vector<std::string> lastRawLines = RuntimeConfigFile::Get().cheatCodes;
    auto rawLines = RuntimeConfigFile::ReloadCheatCodes();
    if (rawLines == lastRawLines) {
        return;
    }
    lastRawLines = rawLines;
    auto parsed = ParseAllCodes(rawLines);
    std::cout << "[cheats] Config.toml changed - " << parsed.size() << " simple RAM-write(s) active"
              << std::endl;
    ParsedCodes() = std::move(parsed);
}

// Call once per VBlank (see vi.cpp AdvanceRetrace), not once at boot - see the file comment.
inline void ApplyAll() {
    // A full poll (file read + TOML reparse) is wasted work almost every call, since this only
    // ever changes because a human clicked a checkbox in another process - roughly once a second
    // (assuming ~60 VBlanks/sec; PAL's ~50 just makes this a little over a second) keeps a toggle
    // feeling near-instant without doing that on every single frame.
    static uint32_t framesSincePoll = 0;
    if (++framesSincePoll >= 60) {
        framesSincePoll = 0;
        PollForChanges();
    }

    const auto& codes = ParsedCodes();
    if (codes.empty()) {
        return;
    }
    for (const auto& code : codes) {
        if (!Memory::Contains(code.address, code.widthBytes)) {
            continue;
        }
        switch (code.widthBytes) {
            case 1:
                Memory::Write8(code.address, static_cast<uint8_t>(code.value));
                break;
            case 2:
                Memory::Write16(code.address, static_cast<uint16_t>(code.value));
                break;
            case 4:
                Memory::Write32(code.address, code.value);
                break;
            default:
                break;
        }
    }
}

}  // namespace CheatCodes
