# Classic model exporter: links the original engine (GPLv3 source release) to read models.bin
# from your own copy of the game. Build with MSYS2 MinGW64, see README.md.
cmake_minimum_required(VERSION 3.16)
project(classic_export C CXX)

set(MACHINES_SOURCE "" CACHE PATH "Folder of the original engine source (nightdivestudio/machines)")
if(NOT EXISTS "${MACHINES_SOURCE}/src/CMakeLists.txt")
    message(FATAL_ERROR "Set MACHINES_SOURCE to the original engine source folder")
endif()

# Today's GCC needs these headers spelt out; the original sources stay unchanged.
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -include cstdint")
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -include stdlib.h")
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)

# The engine's object libraries (target "libs"), its exe targets stay unbuilt.
add_subdirectory("${MACHINES_SOURCE}" engine EXCLUDE_FROM_ALL)

list(APPEND CMAKE_MODULE_PATH "${MACHINES_SOURCE}/cmake")
find_package(OpenGL REQUIRED)
find_package(SDL2 REQUIRED)
find_package(SDL2_image REQUIRED)
find_package(GLEW REQUIRED)
find_package(OpenAL REQUIRED)
find_package(Alure REQUIRED)
find_package(Freetype REQUIRED)

add_executable(classic_export exportapp.cpp)
target_include_directories(classic_export PRIVATE
    "${MACHINES_SOURCE}/include/libdev"
    "${MACHINES_SOURCE}/include/projects"
    "${MACHINES_SOURCE}/external"
    ${SDL2_INCLUDE_DIR} ${GLEW_INCLUDE_PATH})
# Reads RenMesh's private vertex and triangle data.
target_compile_options(classic_export PRIVATE -std=gnu++11 -m64 -fno-access-control -w)
target_link_libraries(classic_export
    libs libaudio
    ${SDL2_LIBRARY} ${OPENGL_LIBRARY} ${GLEW_LIBRARY} ${SDL2_IMAGE_LIBRARY}
    ${ALURE_LIBRARY} ${OPENAL_LIBRARY} ${FREETYPE_LIBRARY}
    "${MACHINES_SOURCE}/windows/lib64/libenet.dll.a" ws2_32 winmm
    -static-libgcc -static-libstdc++)
