/* Single translation unit for header-only media codecs (stb / dr_libs). */
#define STB_IMAGE_IMPLEMENTATION
#define STBI_NO_HDR
#define STBI_NO_LINEAR
#define STBI_ONLY_JPEG
#define STBI_ONLY_PNG
#define STBI_ONLY_BMP
#define STBI_ONLY_GIF
#define STBI_ONLY_TGA
#include "../third_party/media/stb_image.h"

#define DR_WAV_IMPLEMENTATION
#include "../third_party/media/dr_wav.h"

#define DR_MP3_IMPLEMENTATION
#include "../third_party/media/dr_mp3.h"

#define DR_FLAC_IMPLEMENTATION
#include "../third_party/media/dr_flac.h"
