cmake_minimum_required(VERSION 3.16)

project(Texture2DDecoderNative CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_library(Texture2DDecoderNative SHARED
    dllmain.cpp
    astc.cpp
    atc.cpp
    bcn.cpp
    crunch.cpp
    etc.cpp
    pvrtc.cpp
    unitycrunch.cpp
)

target_include_directories(Texture2DDecoderNative PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/crunch
    ${CMAKE_CURRENT_SOURCE_DIR}/unitycrunch
    ${CMAKE_CURRENT_SOURCE_DIR}/fp16
)

target_compile_definitions(Texture2DDecoderNative PRIVATE _T2D_DLL)

if(ANDROID)
    target_link_options(Texture2DDecoderNative PRIVATE "-Wl,-z,max-page-size=16384")
endif()

