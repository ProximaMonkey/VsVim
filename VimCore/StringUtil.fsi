﻿#light

namespace VimCore

module StringUtil =

    val FindFirst : seq<char> -> int -> (char -> bool) -> option<int * char>
    val CharAtOption : string -> int -> option<char>
    val CharAt : string -> int -> char
    val IsValidIndex : string -> int -> bool
    val Repeat : string -> int -> string
    
