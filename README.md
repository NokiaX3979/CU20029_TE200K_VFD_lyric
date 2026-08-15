# CU20029_TE200K_VFD_lyric
a not simple vfd lyric demo (it just works(on my computer))  
Based on this project: [https://github.com/DubyaDude/WindowsMediaController](https://github.com/DubyaDude/WindowsMediaController)
Plan to rewrite to C# later due to SMTC

## What you need:
1. A VFD screen CU20029-TE200K (or Futaba M202MD28A maybe also work)  
2. a pi rp2040 (or any ttl to rs232 module, the rp2040 only convert the ttl signal to rs232 signal)  
3. python interpreter

## What music software support?
Maybe all music software that suppoerts SMTC procotool?  
Foobar2000 or Windows Groove music is the best(both support artist broadcast over SMTC)  
(some programs doesnt broadcast song name in smtc, others may dont send time info in smtc, in that case it only send lyric based internal time and the jump doesnt work.
