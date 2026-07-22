Óa
fD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.Wpf\SessionListItem.cs
	namespace		 	
Titanium		
 
.		 
Web		 
.		 
Proxy		 
.		 
Examples		 %
.		% &
Wpf		& )
{

 
public 

class 
SessionListItem  
:! ""
INotifyPropertyChanged# 9
{ 
private 
long 
? 
bodySize 
; 
private 
Guid 
clientConnectionId '
;' (
private 
	Exception 
	exception #
;# $
private 
string 
host 
; 
private 
int 
	processId 
; 
private 
string 
protocol 
;  
private 
long 
receivedDataCount &
;& '
private 
long 
sentDataCount "
;" #
private 
Guid 
serverConnectionId '
;' (
private 
string 

statusCode !
;! "
private 
string 
url 
; 
public 
int 
Number 
{ 
get 
;  
set! $
;$ %
}& '
public 
Guid 
ClientConnectionId &
{ 	
get 
=> 
clientConnectionId %
;% &
set 
=> 
SetField 
( 
ref 
clientConnectionId  2
,2 3
value4 9
)9 :
;: ;
} 	
public!! 
Guid!! 
ServerConnectionId!! &
{"" 	
get## 
=>## 
serverConnectionId## %
;##% &
set$$ 
=>$$ 
SetField$$ 
($$ 
ref$$ 
serverConnectionId$$  2
,$$2 3
value$$4 9
)$$9 :
;$$: ;
}%% 	
public'' 
HttpWebClient'' 

HttpClient'' '
{''( )
get''* -
;''- .
set''/ 2
;''2 3
}''4 5
public)) 

IPEndPoint)) 
ClientLocalEndPoint)) -
{)). /
get))0 3
;))3 4
set))5 8
;))8 9
})): ;
public++ 

IPEndPoint++  
ClientRemoteEndPoint++ .
{++/ 0
get++1 4
;++4 5
set++6 9
;++9 :
}++; <
public-- 
bool-- 
IsTunnelConnect-- #
{--$ %
get--& )
;--) *
set--+ .
;--. /
}--0 1
public// 
string// 

StatusCode//  
{00 	
get11 
=>11 

statusCode11 
;11 
set22 
=>22 
SetField22 
(22 
ref22 

statusCode22  *
,22* +
value22, 1
)221 2
;222 3
}33 	
public55 
string55 
Protocol55 
{66 	
get77 
=>77 
protocol77 
;77 
set88 
=>88 
SetField88 
(88 
ref88 
protocol88  (
,88( )
value88* /
)88/ 0
;880 1
}99 	
public;; 
string;; 
Host;; 
{<< 	
get== 
=>== 
host== 
;== 
set>> 
=>>> 
SetField>> 
(>> 
ref>> 
host>>  $
,>>$ %
value>>& +
)>>+ ,
;>>, -
}?? 	
publicAA 
stringAA 
UrlAA 
{BB 	
getCC 
=>CC 
urlCC 
;CC 
setDD 
=>DD 
SetFieldDD 
(DD 
refDD 
urlDD  #
,DD# $
valueDD% *
)DD* +
;DD+ ,
}EE 	
publicGG 
longGG 
?GG 
BodySizeGG 
{HH 	
getII 
=>II 
bodySizeII 
;II 
setJJ 
=>JJ 
SetFieldJJ 
(JJ 
refJJ 
bodySizeJJ  (
,JJ( )
valueJJ* /
)JJ/ 0
;JJ0 1
}KK 	
publicMM 
intMM 
	ProcessIdMM 
{NN 	
getOO 
=>OO 
	processIdOO 
;OO 
setPP 
{QQ 
ifRR 
(RR 
SetFieldRR 
(RR 
refRR  
	processIdRR! *
,RR* +
valueRR, 1
)RR1 2
)RR2 3
OnPropertyChangedRR4 E
(RRE F
nameofRRF L
(RRL M
ProcessRRM T
)RRT U
)RRU V
;RRV W
}SS 
}TT 	
publicVV 
stringVV 
ProcessVV 
{WW 	
getXX 
{YY 
tryZZ 
{[[ 
var\\ 
process\\ 
=\\  !
System\\" (
.\\( )
Diagnostics\\) 4
.\\4 5
Process\\5 <
.\\< =
GetProcessById\\= K
(\\K L
	processId\\L U
)\\U V
;\\V W
return]] 
process]] "
.]]" #
ProcessName]]# .
+]]/ 0
$str]]1 4
+]]5 6
	processId]]7 @
;]]@ A
}^^ 
catch__ 
(__ 
	Exception__  
)__  !
{`` 
returnaa 
stringaa !
.aa! "
Emptyaa" '
;aa' (
}bb 
}cc 
}dd 	
publicff 
longff 
ReceivedDataCountff %
{gg 	
gethh 
=>hh 
receivedDataCounthh $
;hh$ %
setii 
=>ii 
SetFieldii 
(ii 
refii 
receivedDataCountii  1
,ii1 2
valueii3 8
)ii8 9
;ii9 :
}jj 	
publicll 
longll 
SentDataCountll !
{mm 	
getnn 
=>nn 
sentDataCountnn  
;nn  !
setoo 
=>oo 
SetFieldoo 
(oo 
refoo 
sentDataCountoo  -
,oo- .
valueoo/ 4
)oo4 5
;oo5 6
}pp 	
publicrr 
	Exceptionrr 
	Exceptionrr "
{ss 	
gettt 
=>tt 
	exceptiontt 
;tt 
setuu 
=>uu 
SetFielduu 
(uu 
refuu 
	exceptionuu  )
,uu) *
valueuu+ 0
)uu0 1
;uu1 2
}vv 	
publicxx 
eventxx '
PropertyChangedEventHandlerxx 0
PropertyChangedxx1 @
;xx@ A
	protectedzz 
boolzz 
SetFieldzz 
<zz  
Tzz  !
>zz! "
(zz" #
refzz# &
Tzz' (
fieldzz) .
,zz. /
Tzz0 1
valuezz2 7
,zz7 8
[zz9 :
CallerMemberNamezz: J
]zzJ K
stringzzL R
propertyNamezzS _
=zz` a
nullzzb f
)zzf g
{{{ 	
if|| 
(|| 
!|| 
Equals|| 
(|| 
field|| 
,|| 
value|| $
)||$ %
)||% &
{}} 
field~~ 
=~~ 
value~~ 
;~~ 
OnPropertyChanged !
(! "
propertyName" .
). /
;/ 0
return
ÄÄ 
true
ÄÄ 
;
ÄÄ 
}
ÅÅ 
return
ÉÉ 
false
ÉÉ 
;
ÉÉ 
}
ÑÑ 	
[
ÜÜ 	,
NotifyPropertyChangedInvocator
ÜÜ	 '
]
ÜÜ' (
	protected
áá 
virtual
áá 
void
áá 
OnPropertyChanged
áá 0
(
áá0 1
[
áá1 2
CallerMemberName
áá2 B
]
ááB C
string
ááD J
propertyName
ááK W
=
ááX Y
null
ááZ ^
)
áá^ _
{
àà 	
PropertyChanged
ââ 
?
ââ 
.
ââ 
Invoke
ââ #
(
ââ# $
this
ââ$ (
,
ââ( )
new
ââ* -&
PropertyChangedEventArgs
ââ. F
(
ââF G
propertyName
ââG S
)
ââS T
)
ââT U
;
ââU V
}
ää 	
public
åå 
void
åå 
Update
åå 
(
åå "
SessionEventArgsBase
åå /
args
åå0 4
)
åå4 5
{
çç 	
var
éé 
request
éé 
=
éé 

HttpClient
éé $
.
éé$ %
Request
éé% ,
;
éé, -
var
èè 
response
èè 
=
èè 

HttpClient
èè %
.
èè% &
Response
èè& .
;
èè. /
var
êê 

statusCode
êê 
=
êê 
response
êê %
?
êê% &
.
êê& '

StatusCode
êê' 1
??
êê2 4
$num
êê5 6
;
êê6 7

StatusCode
ëë 
=
ëë 

statusCode
ëë #
==
ëë$ &
$num
ëë' (
?
ëë) *
$str
ëë+ .
:
ëë/ 0

statusCode
ëë1 ;
.
ëë; <
ToString
ëë< D
(
ëëD E
)
ëëE F
;
ëëF G
Protocol
íí 
=
íí 
request
íí 
.
íí 
HttpVersion
íí *
.
íí* +
Major
íí+ 0
==
íí1 3
$num
íí4 5
?
íí6 7
$str
íí8 ?
:
íí@ A
request
ííB I
.
ííI J

RequestUri
ííJ T
.
ííT U
Scheme
ííU [
;
íí[ \ 
ClientConnectionId
ìì 
=
ìì  
args
ìì! %
.
ìì% & 
ClientConnectionId
ìì& 8
;
ìì8 9 
ServerConnectionId
îî 
=
îî  
args
îî! %
.
îî% & 
ServerConnectionId
îî& 8
;
îî8 9
if
ññ 
(
ññ 
IsTunnelConnect
ññ 
)
ññ  
{
óó 
Host
òò 
=
òò 
$str
òò "
;
òò" #
Url
ôô 
=
ôô 
request
ôô 
.
ôô 

RequestUri
ôô (
.
ôô( )
Host
ôô) -
+
ôô. /
$str
ôô0 3
+
ôô4 5
request
ôô6 =
.
ôô= >

RequestUri
ôô> H
.
ôôH I
Port
ôôI M
;
ôôM N
}
öö 
else
õõ 
{
úú 
Host
ùù 
=
ùù 
request
ùù 
.
ùù 

RequestUri
ùù )
.
ùù) *
Host
ùù* .
;
ùù. /
Url
ûû 
=
ûû 
request
ûû 
.
ûû 

RequestUri
ûû (
.
ûû( )
AbsolutePath
ûû) 5
;
ûû5 6
}
üü 
if
°° 
(
°° 
!
°° 
IsTunnelConnect
°°  
)
°°  !
{
¢¢ 
long
££ 
responseSize
££ !
=
££" #
-
££$ %
$num
££% &
;
££& '
if
§§ 
(
§§ 
response
§§ 
!=
§§ 
null
§§  $
)
§§$ %
{
•• 
if
¶¶ 
(
¶¶ 
response
¶¶  
.
¶¶  !
ContentLength
¶¶! .
!=
¶¶/ 1
-
¶¶2 3
$num
¶¶3 4
)
¶¶4 5
responseSize
ßß $
=
ßß% &
response
ßß' /
.
ßß/ 0
ContentLength
ßß0 =
;
ßß= >
else
®® 
if
®® 
(
®® 
response
®® %
.
®®% &

IsBodyRead
®®& 0
&&
®®1 3
response
®®4 <
.
®®< =
Body
®®= A
!=
®®B D
null
®®E I
)
®®I J
responseSize
®®K W
=
®®X Y
response
®®Z b
.
®®b c
Body
®®c g
.
®®g h
Length
®®h n
;
®®n o
}
©© 
BodySize
´´ 
=
´´ 
responseSize
´´ '
;
´´' (
}
¨¨ 
	ProcessId
ÆÆ 
=
ÆÆ 

HttpClient
ÆÆ "
.
ÆÆ" #
	ProcessId
ÆÆ# ,
.
ÆÆ, -
Value
ÆÆ- 2
;
ÆÆ2 3
}
ØØ 	
}
∞∞ 
}±± øµ
mD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.Wpf\Properties\Annotations.cs
	namespace!! 	
Titanium!!
 
.!! 
Web!! 
.!! 
Proxy!! 
.!! 
Examples!! %
.!!% &
Wpf!!& )
.!!) *
Annotations!!* 5
{"" 
[11 
AttributeUsage11 
(11 
AttributeTargets22 
.22 
Method22 
|22  !
AttributeTargets22" 2
.222 3
	Parameter223 <
|22= >
AttributeTargets22? O
.22O P
Property22P X
|22Y Z
AttributeTargets33 
.33 
Delegate33 !
|33" #
AttributeTargets33$ 4
.334 5
Field335 :
|33; <
AttributeTargets33= M
.33M N
Event33N S
|33T U
AttributeTargets44 
.44 
Class44 
|44  
AttributeTargets44! 1
.441 2
	Interface442 ;
|44< =
AttributeTargets44> N
.44N O
GenericParameter44O _
)44_ `
]44` a
public55 

sealed55 
class55 
CanBeNullAttribute55 *
:55+ ,
	Attribute55- 6
{66 
}77 
[CC 
AttributeUsageCC 
(CC 
AttributeTargetsDD 
.DD 
MethodDD 
|DD  !
AttributeTargetsDD" 2
.DD2 3
	ParameterDD3 <
|DD= >
AttributeTargetsDD? O
.DDO P
PropertyDDP X
|DDY Z
AttributeTargetsEE 
.EE 
DelegateEE !
|EE" #
AttributeTargetsEE$ 4
.EE4 5
FieldEE5 :
|EE; <
AttributeTargetsEE= M
.EEM N
EventEEN S
|EET U
AttributeTargetsFF 
.FF 
ClassFF 
|FF  
AttributeTargetsFF! 1
.FF1 2
	InterfaceFF2 ;
|FF< =
AttributeTargetsFF> N
.FFN O
GenericParameterFFO _
)FF_ `
]FF` a
publicGG 

sealedGG 
classGG 
NotNullAttributeGG (
:GG) *
	AttributeGG+ 4
{HH 
}II 
[PP 
AttributeUsagePP 
(PP 
AttributeTargetsQQ 
.QQ 
MethodQQ 
|QQ  !
AttributeTargetsQQ" 2
.QQ2 3
	ParameterQQ3 <
|QQ= >
AttributeTargetsQQ? O
.QQO P
PropertyQQP X
|QQY Z
AttributeTargetsRR 
.RR 
DelegateRR !
|RR" #
AttributeTargetsRR$ 4
.RR4 5
FieldRR5 :
)RR: ;
]RR; <
publicSS 

sealedSS 
classSS  
ItemNotNullAttributeSS ,
:SS- .
	AttributeSS/ 8
{TT 
}UU 
[\\ 
AttributeUsage\\ 
(\\ 
AttributeTargets]] 
.]] 
Method]] 
|]]  !
AttributeTargets]]" 2
.]]2 3
	Parameter]]3 <
|]]= >
AttributeTargets]]? O
.]]O P
Property]]P X
|]]Y Z
AttributeTargets^^ 
.^^ 
Delegate^^ !
|^^" #
AttributeTargets^^$ 4
.^^4 5
Field^^5 :
)^^: ;
]^^; <
public__ 

sealed__ 
class__ "
ItemCanBeNullAttribute__ .
:__/ 0
	Attribute__1 :
{`` 
}aa 
[rr 
AttributeUsagerr 
(rr 
AttributeTargetsss 
.ss 
Constructorss $
|ss% &
AttributeTargetsss' 7
.ss7 8
Methodss8 >
|ss? @
AttributeTargetstt 
.tt 
Propertytt !
|tt" #
AttributeTargetstt$ 4
.tt4 5
Delegatett5 =
)tt= >
]tt> ?
publicuu 

sealeduu 
classuu '
StringFormatMethodAttributeuu 3
:uu4 5
	Attributeuu6 ?
{vv 
publiczz '
StringFormatMethodAttributezz *
(zz* +
[zz+ ,
NotNullzz, 3
]zz3 4
stringzz5 ;
formatParameterNamezz< O
)zzO P
{{{ 	
FormatParameterName|| 
=||  !
formatParameterName||" 5
;||5 6
}}} 	
[ 	
NotNull	 
] 
public 
string 
FormatParameterName  3
{4 5
get6 9
;9 :
}; <
}
ÄÄ 
[
ÜÜ 
AttributeUsage
ÜÜ 
(
ÜÜ 
AttributeTargets
áá 
.
áá 
	Parameter
áá "
|
áá# $
AttributeTargets
áá% 5
.
áá5 6
Property
áá6 >
|
áá? @
AttributeTargets
ááA Q
.
ááQ R
Field
ááR W
,
ááW X
AllowMultiple
àà 
=
àà 
true
àà 
)
àà 
]
àà 
public
ââ 

sealed
ââ 
class
ââ $
ValueProviderAttribute
ââ .
:
ââ/ 0
	Attribute
ââ1 :
{
ää 
public
ãã $
ValueProviderAttribute
ãã %
(
ãã% &
[
ãã& '
NotNull
ãã' .
]
ãã. /
string
ãã0 6
name
ãã7 ;
)
ãã; <
{
åå 	
Name
çç 
=
çç 
name
çç 
;
çç 
}
éé 	
[
êê 	
NotNull
êê	 
]
êê 
public
êê 
string
êê 
Name
êê  $
{
êê% &
get
êê' *
;
êê* +
}
êê, -
}
ëë 
[
†† 
AttributeUsage
†† 
(
†† 
AttributeTargets
†† $
.
††$ %
	Parameter
††% .
)
††. /
]
††/ 0
public
°° 

sealed
°° 
class
°° +
InvokerParameterNameAttribute
°° 5
:
°°6 7
	Attribute
°°8 A
{
¢¢ 
}
££ 
[
ﬁﬁ 
AttributeUsage
ﬁﬁ 
(
ﬁﬁ 
AttributeTargets
ﬁﬁ $
.
ﬁﬁ$ %
Method
ﬁﬁ% +
)
ﬁﬁ+ ,
]
ﬁﬁ, -
public
ﬂﬂ 

sealed
ﬂﬂ 
class
ﬂﬂ 5
'NotifyPropertyChangedInvocatorAttribute
ﬂﬂ ?
:
ﬂﬂ@ A
	Attribute
ﬂﬂB K
{
‡‡ 
public
·· 5
'NotifyPropertyChangedInvocatorAttribute
·· 6
(
··6 7
)
··7 8
{
‚‚ 	
}
„„ 	
public
ÂÂ 5
'NotifyPropertyChangedInvocatorAttribute
ÂÂ 6
(
ÂÂ6 7
[
ÂÂ7 8
NotNull
ÂÂ8 ?
]
ÂÂ? @
string
ÂÂA G
parameterName
ÂÂH U
)
ÂÂU V
{
ÊÊ 	
ParameterName
ÁÁ 
=
ÁÁ 
parameterName
ÁÁ )
;
ÁÁ) *
}
ËË 	
[
ÍÍ 	
	CanBeNull
ÍÍ	 
]
ÍÍ 
public
ÍÍ 
string
ÍÍ !
ParameterName
ÍÍ" /
{
ÍÍ0 1
get
ÍÍ2 5
;
ÍÍ5 6
}
ÍÍ7 8
}
ÎÎ 
[
•• 
AttributeUsage
•• 
(
•• 
AttributeTargets
•• $
.
••$ %
Method
••% +
,
••+ ,
AllowMultiple
••- :
=
••; <
true
••= A
)
••A B
]
••B C
public
¶¶ 

sealed
¶¶ 
class
¶¶ )
ContractAnnotationAttribute
¶¶ 3
:
¶¶4 5
	Attribute
¶¶6 ?
{
ßß 
public
®® )
ContractAnnotationAttribute
®® *
(
®®* +
[
®®+ ,
NotNull
®®, 3
]
®®3 4
string
®®5 ;
contract
®®< D
)
®®D E
:
©© 
this
©© 
(
©© 
contract
©© 
,
©© 
false
©© "
)
©©" #
{
™™ 	
}
´´ 	
public
≠≠ )
ContractAnnotationAttribute
≠≠ *
(
≠≠* +
[
≠≠+ ,
NotNull
≠≠, 3
]
≠≠3 4
string
≠≠5 ;
contract
≠≠< D
,
≠≠D E
bool
≠≠F J
forceFullStates
≠≠K Z
)
≠≠Z [
{
ÆÆ 	
Contract
ØØ 
=
ØØ 
contract
ØØ 
;
ØØ  
ForceFullStates
∞∞ 
=
∞∞ 
forceFullStates
∞∞ -
;
∞∞- .
}
±± 	
[
≥≥ 	
NotNull
≥≥	 
]
≥≥ 
public
≥≥ 
string
≥≥ 
Contract
≥≥  (
{
≥≥) *
get
≥≥+ .
;
≥≥. /
}
≥≥0 1
public
µµ 
bool
µµ 
ForceFullStates
µµ #
{
µµ$ %
get
µµ& )
;
µµ) *
}
µµ+ ,
}
∂∂ 
[
√√ 
AttributeUsage
√√ 
(
√√ 
AttributeTargets
√√ $
.
√√$ %
All
√√% (
)
√√( )
]
√√) *
public
ƒƒ 

sealed
ƒƒ 
class
ƒƒ +
LocalizationRequiredAttribute
ƒƒ 5
:
ƒƒ6 7
	Attribute
ƒƒ8 A
{
≈≈ 
public
∆∆ +
LocalizationRequiredAttribute
∆∆ ,
(
∆∆, -
)
∆∆- .
:
∆∆/ 0
this
∆∆1 5
(
∆∆5 6
true
∆∆6 :
)
∆∆: ;
{
«« 	
}
»» 	
public
   +
LocalizationRequiredAttribute
   ,
(
  , -
bool
  - 1
required
  2 :
)
  : ;
{
ÀÀ 	
Required
ÃÃ 
=
ÃÃ 
required
ÃÃ 
;
ÃÃ  
}
ÕÕ 	
public
œœ 
bool
œœ 
Required
œœ 
{
œœ 
get
œœ "
;
œœ" #
}
œœ$ %
}
–– 
[
ËË 
AttributeUsage
ËË 
(
ËË 
AttributeTargets
ËË $
.
ËË$ %
	Interface
ËË% .
|
ËË/ 0
AttributeTargets
ËË1 A
.
ËËA B
Class
ËËB G
|
ËËH I
AttributeTargets
ËËJ Z
.
ËËZ [
Struct
ËË[ a
)
ËËa b
]
ËËb c
public
ÈÈ 

sealed
ÈÈ 
class
ÈÈ 2
$CannotApplyEqualityOperatorAttribute
ÈÈ <
:
ÈÈ= >
	Attribute
ÈÈ? H
{
ÍÍ 
}
ÎÎ 
[
˙˙ 
AttributeUsage
˙˙ 
(
˙˙ 
AttributeTargets
˙˙ $
.
˙˙$ %
Class
˙˙% *
,
˙˙* +
AllowMultiple
˙˙, 9
=
˙˙: ;
true
˙˙< @
)
˙˙@ A
]
˙˙A B
[
˚˚ 
BaseTypeRequired
˚˚ 
(
˚˚ 
typeof
˚˚ 
(
˚˚ 
	Attribute
˚˚ &
)
˚˚& '
)
˚˚' (
]
˚˚( )
public
¸¸ 

sealed
¸¸ 
class
¸¸ '
BaseTypeRequiredAttribute
¸¸ 1
:
¸¸2 3
	Attribute
¸¸4 =
{
˝˝ 
public
˛˛ '
BaseTypeRequiredAttribute
˛˛ (
(
˛˛( )
[
˛˛) *
NotNull
˛˛* 1
]
˛˛1 2
Type
˛˛3 7
baseType
˛˛8 @
)
˛˛@ A
{
ˇˇ 	
BaseType
ÄÄ 
=
ÄÄ 
baseType
ÄÄ 
;
ÄÄ  
}
ÅÅ 	
[
ÉÉ 	
NotNull
ÉÉ	 
]
ÉÉ 
public
ÉÉ 
Type
ÉÉ 
BaseType
ÉÉ &
{
ÉÉ' (
get
ÉÉ) ,
;
ÉÉ, -
}
ÉÉ. /
}
ÑÑ 
[
ää 
AttributeUsage
ää 
(
ää 
AttributeTargets
ää $
.
ää$ %
All
ää% (
)
ää( )
]
ää) *
public
ãã 

sealed
ãã 
class
ãã %
UsedImplicitlyAttribute
ãã /
:
ãã0 1
	Attribute
ãã2 ;
{
åå 
public
çç %
UsedImplicitlyAttribute
çç &
(
çç& '
)
çç' (
:
éé 
this
éé 
(
éé "
ImplicitUseKindFlags
éé '
.
éé' (
Default
éé( /
,
éé/ 0$
ImplicitUseTargetFlags
éé1 G
.
ééG H
Default
ééH O
)
ééO P
{
èè 	
}
êê 	
public
íí %
UsedImplicitlyAttribute
íí &
(
íí& '"
ImplicitUseKindFlags
íí' ;
useKindFlags
íí< H
)
ííH I
:
ìì 
this
ìì 
(
ìì 
useKindFlags
ìì 
,
ìì  $
ImplicitUseTargetFlags
ìì! 7
.
ìì7 8
Default
ìì8 ?
)
ìì? @
{
îî 	
}
ïï 	
public
óó %
UsedImplicitlyAttribute
óó &
(
óó& '$
ImplicitUseTargetFlags
óó' =
targetFlags
óó> I
)
óóI J
:
òò 
this
òò 
(
òò "
ImplicitUseKindFlags
òò '
.
òò' (
Default
òò( /
,
òò/ 0
targetFlags
òò1 <
)
òò< =
{
ôô 	
}
öö 	
public
úú %
UsedImplicitlyAttribute
úú &
(
úú& '"
ImplicitUseKindFlags
úú' ;
useKindFlags
úú< H
,
úúH I$
ImplicitUseTargetFlags
úúJ `
targetFlags
úúa l
)
úúl m
{
ùù 	
UseKindFlags
ûû 
=
ûû 
useKindFlags
ûû '
;
ûû' (
TargetFlags
üü 
=
üü 
targetFlags
üü %
;
üü% &
}
†† 	
public
¢¢ "
ImplicitUseKindFlags
¢¢ #
UseKindFlags
¢¢$ 0
{
¢¢1 2
get
¢¢3 6
;
¢¢6 7
}
¢¢8 9
public
§§ $
ImplicitUseTargetFlags
§§ %
TargetFlags
§§& 1
{
§§2 3
get
§§4 7
;
§§7 8
}
§§9 :
}
•• 
[
´´ 
AttributeUsage
´´ 
(
´´ 
AttributeTargets
´´ $
.
´´$ %
Class
´´% *
|
´´+ ,
AttributeTargets
´´- =
.
´´= >
GenericParameter
´´> N
)
´´N O
]
´´O P
public
¨¨ 

sealed
¨¨ 
class
¨¨ '
MeansImplicitUseAttribute
¨¨ 1
:
¨¨2 3
	Attribute
¨¨4 =
{
≠≠ 
public
ÆÆ '
MeansImplicitUseAttribute
ÆÆ (
(
ÆÆ( )
)
ÆÆ) *
:
ØØ 
this
ØØ 
(
ØØ "
ImplicitUseKindFlags
ØØ '
.
ØØ' (
Default
ØØ( /
,
ØØ/ 0$
ImplicitUseTargetFlags
ØØ1 G
.
ØØG H
Default
ØØH O
)
ØØO P
{
∞∞ 	
}
±± 	
public
≥≥ '
MeansImplicitUseAttribute
≥≥ (
(
≥≥( )"
ImplicitUseKindFlags
≥≥) =
useKindFlags
≥≥> J
)
≥≥J K
:
¥¥ 
this
¥¥ 
(
¥¥ 
useKindFlags
¥¥ 
,
¥¥  $
ImplicitUseTargetFlags
¥¥! 7
.
¥¥7 8
Default
¥¥8 ?
)
¥¥? @
{
µµ 	
}
∂∂ 	
public
∏∏ '
MeansImplicitUseAttribute
∏∏ (
(
∏∏( )$
ImplicitUseTargetFlags
∏∏) ?
targetFlags
∏∏@ K
)
∏∏K L
:
ππ 
this
ππ 
(
ππ "
ImplicitUseKindFlags
ππ '
.
ππ' (
Default
ππ( /
,
ππ/ 0
targetFlags
ππ1 <
)
ππ< =
{
∫∫ 	
}
ªª 	
public
ΩΩ '
MeansImplicitUseAttribute
ΩΩ (
(
ΩΩ( )"
ImplicitUseKindFlags
ΩΩ) =
useKindFlags
ΩΩ> J
,
ΩΩJ K$
ImplicitUseTargetFlags
ΩΩL b
targetFlags
ΩΩc n
)
ΩΩn o
{
ææ 	
UseKindFlags
øø 
=
øø 
useKindFlags
øø '
;
øø' (
TargetFlags
¿¿ 
=
¿¿ 
targetFlags
¿¿ %
;
¿¿% &
}
¡¡ 	
[
√√ 	
UsedImplicitly
√√	 
]
√√ 
public
√√ "
ImplicitUseKindFlags
√√  4
UseKindFlags
√√5 A
{
√√B C
get
√√D G
;
√√G H
}
√√I J
[
≈≈ 	
UsedImplicitly
≈≈	 
]
≈≈ 
public
≈≈ $
ImplicitUseTargetFlags
≈≈  6
TargetFlags
≈≈7 B
{
≈≈C D
get
≈≈E H
;
≈≈H I
}
≈≈J K
}
∆∆ 
[
»» 
Flags
»» 

]
»»
 
public
…… 

enum
…… "
ImplicitUseKindFlags
…… $
{
   
Default
ÀÀ 
=
ÀÀ 
Access
ÀÀ 
|
ÀÀ 
Assign
ÀÀ !
|
ÀÀ" #7
)InstantiatedWithFixedConstructorSignature
ÀÀ$ M
,
ÀÀM N
Access
ŒŒ 
=
ŒŒ 
$num
ŒŒ 
,
ŒŒ 
Assign
—— 
=
—— 
$num
—— 
,
—— 7
)InstantiatedWithFixedConstructorSignature
◊◊ 1
=
◊◊2 3
$num
◊◊4 5
,
◊◊5 65
'InstantiatedNoFixedConstructorSignature
⁄⁄ /
=
⁄⁄0 1
$num
⁄⁄2 3
}
€€ 
[
·· 
Flags
·· 

]
··
 
public
‚‚ 

enum
‚‚ $
ImplicitUseTargetFlags
‚‚ &
{
„„ 
Default
‰‰ 
=
‰‰ 
Itself
‰‰ 
,
‰‰ 
Itself
ÂÂ 
=
ÂÂ 
$num
ÂÂ 
,
ÂÂ 
Members
ËË 
=
ËË 
$num
ËË 
,
ËË 
WithMembers
ÎÎ 
=
ÎÎ 
Itself
ÎÎ 
|
ÎÎ 
Members
ÎÎ &
}
ÏÏ 
[
ÚÚ 
MeansImplicitUse
ÚÚ 
(
ÚÚ $
ImplicitUseTargetFlags
ÚÚ ,
.
ÚÚ, -
WithMembers
ÚÚ- 8
)
ÚÚ8 9
]
ÚÚ9 :
public
ÛÛ 

sealed
ÛÛ 
class
ÛÛ  
PublicAPIAttribute
ÛÛ *
:
ÛÛ+ ,
	Attribute
ÛÛ- 6
{
ÙÙ 
public
ıı  
PublicAPIAttribute
ıı !
(
ıı! "
)
ıı" #
{
ˆˆ 	
}
˜˜ 	
public
˘˘  
PublicAPIAttribute
˘˘ !
(
˘˘! "
[
˘˘" #
NotNull
˘˘# *
]
˘˘* +
string
˘˘, 2
comment
˘˘3 :
)
˘˘: ;
{
˙˙ 	
Comment
˚˚ 
=
˚˚ 
comment
˚˚ 
;
˚˚ 
}
¸¸ 	
[
˛˛ 	
	CanBeNull
˛˛	 
]
˛˛ 
public
˛˛ 
string
˛˛ !
Comment
˛˛" )
{
˛˛* +
get
˛˛, /
;
˛˛/ 0
}
˛˛1 2
}
ˇˇ 
[
ÜÜ 
AttributeUsage
ÜÜ 
(
ÜÜ 
AttributeTargets
ÜÜ $
.
ÜÜ$ %
	Parameter
ÜÜ% .
)
ÜÜ. /
]
ÜÜ/ 0
public
áá 

sealed
áá 
class
áá $
InstantHandleAttribute
áá .
:
áá/ 0
	Attribute
áá1 :
{
àà 
}
ââ 
[
òò 
AttributeUsage
òò 
(
òò 
AttributeTargets
òò $
.
òò$ %
Method
òò% +
)
òò+ ,
]
òò, -
public
ôô 

sealed
ôô 
class
ôô 
PureAttribute
ôô %
:
ôô& '
	Attribute
ôô( 1
{
öö 
}
õõ 
[
†† 
AttributeUsage
†† 
(
†† 
AttributeTargets
†† $
.
††$ %
Method
††% +
)
††+ ,
]
††, -
public
°° 

sealed
°° 
class
°° )
MustUseReturnValueAttribute
°° 3
:
°°4 5
	Attribute
°°6 ?
{
¢¢ 
public
££ )
MustUseReturnValueAttribute
££ *
(
££* +
)
££+ ,
{
§§ 	
}
•• 	
public
ßß )
MustUseReturnValueAttribute
ßß *
(
ßß* +
[
ßß+ ,
NotNull
ßß, 3
]
ßß3 4
string
ßß5 ;
justification
ßß< I
)
ßßI J
{
®® 	
Justification
©© 
=
©© 
justification
©© )
;
©©) *
}
™™ 	
[
¨¨ 	
	CanBeNull
¨¨	 
]
¨¨ 
public
¨¨ 
string
¨¨ !
Justification
¨¨" /
{
¨¨0 1
get
¨¨2 5
;
¨¨5 6
}
¨¨7 8
}
≠≠ 
[
¿¿ 
AttributeUsage
¿¿ 
(
¿¿ 
AttributeTargets
¡¡ 
.
¡¡ 
Field
¡¡ 
|
¡¡  
AttributeTargets
¡¡! 1
.
¡¡1 2
Property
¡¡2 :
|
¡¡; <
AttributeTargets
¡¡= M
.
¡¡M N
	Parameter
¡¡N W
|
¡¡X Y
AttributeTargets
¡¡Z j
.
¡¡j k
Method
¡¡k q
|
¡¡r s
AttributeTargets
¬¬ 
.
¬¬ 
Class
¬¬ 
|
¬¬  
AttributeTargets
¬¬! 1
.
¬¬1 2
	Interface
¬¬2 ;
|
¬¬< =
AttributeTargets
¬¬> N
.
¬¬N O
Struct
¬¬O U
|
¬¬V W
AttributeTargets
√√ 
.
√√ 
GenericParameter
√√ )
)
√√) *
]
√√* +
public
ƒƒ 

sealed
ƒƒ 
class
ƒƒ &
ProvidesContextAttribute
ƒƒ 0
:
ƒƒ1 2
	Attribute
ƒƒ3 <
{
≈≈ 
}
∆∆ 
[
ÃÃ 
AttributeUsage
ÃÃ 
(
ÃÃ 
AttributeTargets
ÃÃ $
.
ÃÃ$ %
	Parameter
ÃÃ% .
)
ÃÃ. /
]
ÃÃ/ 0
public
ÕÕ 

sealed
ÕÕ 
class
ÕÕ $
PathReferenceAttribute
ÕÕ .
:
ÕÕ/ 0
	Attribute
ÕÕ1 :
{
ŒŒ 
public
œœ $
PathReferenceAttribute
œœ %
(
œœ% &
)
œœ& '
{
–– 	
}
—— 	
public
”” $
PathReferenceAttribute
”” %
(
””% &
[
””& '
NotNull
””' .
]
””. /
[
””0 1
PathReference
””1 >
]
””> ?
string
””@ F
basePath
””G O
)
””O P
{
‘‘ 	
BasePath
’’ 
=
’’ 
basePath
’’ 
;
’’  
}
÷÷ 	
[
ÿÿ 	
	CanBeNull
ÿÿ	 
]
ÿÿ 
public
ÿÿ 
string
ÿÿ !
BasePath
ÿÿ" *
{
ÿÿ+ ,
get
ÿÿ- 0
;
ÿÿ0 1
}
ÿÿ2 3
}
ŸŸ 
[
ÚÚ 
AttributeUsage
ÚÚ 
(
ÚÚ 
AttributeTargets
ÚÚ $
.
ÚÚ$ %
Method
ÚÚ% +
)
ÚÚ+ ,
]
ÚÚ, -
public
ÛÛ 

sealed
ÛÛ 
class
ÛÛ %
SourceTemplateAttribute
ÛÛ /
:
ÛÛ0 1
	Attribute
ÛÛ2 ;
{
ÙÙ 
}
ıı 
[
ìì 
AttributeUsage
ìì 
(
ìì 
AttributeTargets
ìì $
.
ìì$ %
	Parameter
ìì% .
|
ìì/ 0
AttributeTargets
ìì1 A
.
ììA B
Method
ììB H
,
ììH I
AllowMultiple
ììJ W
=
ììX Y
true
ììZ ^
)
ìì^ _
]
ìì_ `
public
îî 

sealed
îî 
class
îî 
MacroAttribute
îî &
:
îî' (
	Attribute
îî) 2
{
ïï 
[
öö 	
	CanBeNull
öö	 
]
öö 
public
õõ 
string
õõ 

Expression
õõ  
{
õõ! "
get
õõ# &
;
õõ& '
set
õõ( +
;
õõ+ ,
}
õõ- .
public
¶¶ 
int
¶¶ 
Editable
¶¶ 
{
¶¶ 
get
¶¶ !
;
¶¶! "
set
¶¶# &
;
¶¶& '
}
¶¶( )
[
¨¨ 	
	CanBeNull
¨¨	 
]
¨¨ 
public
≠≠ 
string
≠≠ 
Target
≠≠ 
{
≠≠ 
get
≠≠ "
;
≠≠" #
set
≠≠$ '
;
≠≠' (
}
≠≠) *
}
ÆÆ 
[
∞∞ 
AttributeUsage
∞∞ 
(
∞∞ 
AttributeTargets
∞∞ $
.
∞∞$ %
Assembly
∞∞% -
|
∞∞. /
AttributeTargets
∞∞0 @
.
∞∞@ A
Field
∞∞A F
|
∞∞G H
AttributeTargets
∞∞I Y
.
∞∞Y Z
Property
∞∞Z b
,
∞∞b c
AllowMultiple
∞∞d q
=
∞∞r s
true
±± 
)
±± 
]
±± 
public
≤≤ 

sealed
≤≤ 
class
≤≤ 5
'AspMvcAreaMasterLocationFormatAttribute
≤≤ ?
:
≤≤@ A
	Attribute
≤≤B K
{
≥≥ 
public
¥¥ 5
'AspMvcAreaMasterLocationFormatAttribute
¥¥ 6
(
¥¥6 7
[
¥¥7 8
NotNull
¥¥8 ?
]
¥¥? @
string
¥¥A G
format
¥¥H N
)
¥¥N O
{
µµ 	
Format
∂∂ 
=
∂∂ 
format
∂∂ 
;
∂∂ 
}
∑∑ 	
[
ππ 	
NotNull
ππ	 
]
ππ 
public
ππ 
string
ππ 
Format
ππ  &
{
ππ' (
get
ππ) ,
;
ππ, -
}
ππ. /
}
∫∫ 
[
ºº 
AttributeUsage
ºº 
(
ºº 
AttributeTargets
ºº $
.
ºº$ %
Assembly
ºº% -
|
ºº. /
AttributeTargets
ºº0 @
.
ºº@ A
Field
ººA F
|
ººG H
AttributeTargets
ººI Y
.
ººY Z
Property
ººZ b
,
ººb c
AllowMultiple
ººd q
=
ººr s
true
ΩΩ 
)
ΩΩ 
]
ΩΩ 
public
ææ 

sealed
ææ 
class
ææ :
,AspMvcAreaPartialViewLocationFormatAttribute
ææ D
:
ææE F
	Attribute
ææG P
{
øø 
public
¿¿ :
,AspMvcAreaPartialViewLocationFormatAttribute
¿¿ ;
(
¿¿; <
[
¿¿< =
NotNull
¿¿= D
]
¿¿D E
string
¿¿F L
format
¿¿M S
)
¿¿S T
{
¡¡ 	
Format
¬¬ 
=
¬¬ 
format
¬¬ 
;
¬¬ 
}
√√ 	
[
≈≈ 	
NotNull
≈≈	 
]
≈≈ 
public
≈≈ 
string
≈≈ 
Format
≈≈  &
{
≈≈' (
get
≈≈) ,
;
≈≈, -
}
≈≈. /
}
∆∆ 
[
»» 
AttributeUsage
»» 
(
»» 
AttributeTargets
»» $
.
»»$ %
Assembly
»»% -
|
»». /
AttributeTargets
»»0 @
.
»»@ A
Field
»»A F
|
»»G H
AttributeTargets
»»I Y
.
»»Y Z
Property
»»Z b
,
»»b c
AllowMultiple
»»d q
=
»»r s
true
…… 
)
…… 
]
…… 
public
   

sealed
   
class
   3
%AspMvcAreaViewLocationFormatAttribute
   =
:
  > ?
	Attribute
  @ I
{
ÀÀ 
public
ÃÃ 3
%AspMvcAreaViewLocationFormatAttribute
ÃÃ 4
(
ÃÃ4 5
[
ÃÃ5 6
NotNull
ÃÃ6 =
]
ÃÃ= >
string
ÃÃ? E
format
ÃÃF L
)
ÃÃL M
{
ÕÕ 	
Format
ŒŒ 
=
ŒŒ 
format
ŒŒ 
;
ŒŒ 
}
œœ 	
[
—— 	
NotNull
——	 
]
—— 
public
—— 
string
—— 
Format
——  &
{
——' (
get
——) ,
;
——, -
}
——. /
}
““ 
[
‘‘ 
AttributeUsage
‘‘ 
(
‘‘ 
AttributeTargets
‘‘ $
.
‘‘$ %
Assembly
‘‘% -
|
‘‘. /
AttributeTargets
‘‘0 @
.
‘‘@ A
Field
‘‘A F
|
‘‘G H
AttributeTargets
‘‘I Y
.
‘‘Y Z
Property
‘‘Z b
,
‘‘b c
AllowMultiple
‘‘d q
=
‘‘r s
true
’’ 
)
’’ 
]
’’ 
public
÷÷ 

sealed
÷÷ 
class
÷÷ 1
#AspMvcMasterLocationFormatAttribute
÷÷ ;
:
÷÷< =
	Attribute
÷÷> G
{
◊◊ 
public
ÿÿ 1
#AspMvcMasterLocationFormatAttribute
ÿÿ 2
(
ÿÿ2 3
[
ÿÿ3 4
NotNull
ÿÿ4 ;
]
ÿÿ; <
string
ÿÿ= C
format
ÿÿD J
)
ÿÿJ K
{
ŸŸ 	
Format
⁄⁄ 
=
⁄⁄ 
format
⁄⁄ 
;
⁄⁄ 
}
€€ 	
[
›› 	
NotNull
››	 
]
›› 
public
›› 
string
›› 
Format
››  &
{
››' (
get
››) ,
;
››, -
}
››. /
}
ﬁﬁ 
[
‡‡ 
AttributeUsage
‡‡ 
(
‡‡ 
AttributeTargets
‡‡ $
.
‡‡$ %
Assembly
‡‡% -
|
‡‡. /
AttributeTargets
‡‡0 @
.
‡‡@ A
Field
‡‡A F
|
‡‡G H
AttributeTargets
‡‡I Y
.
‡‡Y Z
Property
‡‡Z b
,
‡‡b c
AllowMultiple
‡‡d q
=
‡‡r s
true
·· 
)
·· 
]
·· 
public
‚‚ 

sealed
‚‚ 
class
‚‚ 6
(AspMvcPartialViewLocationFormatAttribute
‚‚ @
:
‚‚A B
	Attribute
‚‚C L
{
„„ 
public
‰‰ 6
(AspMvcPartialViewLocationFormatAttribute
‰‰ 7
(
‰‰7 8
[
‰‰8 9
NotNull
‰‰9 @
]
‰‰@ A
string
‰‰B H
format
‰‰I O
)
‰‰O P
{
ÂÂ 	
Format
ÊÊ 
=
ÊÊ 
format
ÊÊ 
;
ÊÊ 
}
ÁÁ 	
[
ÈÈ 	
NotNull
ÈÈ	 
]
ÈÈ 
public
ÈÈ 
string
ÈÈ 
Format
ÈÈ  &
{
ÈÈ' (
get
ÈÈ) ,
;
ÈÈ, -
}
ÈÈ. /
}
ÍÍ 
[
ÏÏ 
AttributeUsage
ÏÏ 
(
ÏÏ 
AttributeTargets
ÏÏ $
.
ÏÏ$ %
Assembly
ÏÏ% -
|
ÏÏ. /
AttributeTargets
ÏÏ0 @
.
ÏÏ@ A
Field
ÏÏA F
|
ÏÏG H
AttributeTargets
ÏÏI Y
.
ÏÏY Z
Property
ÏÏZ b
,
ÏÏb c
AllowMultiple
ÏÏd q
=
ÏÏr s
true
ÌÌ 
)
ÌÌ 
]
ÌÌ 
public
ÓÓ 

sealed
ÓÓ 
class
ÓÓ /
!AspMvcViewLocationFormatAttribute
ÓÓ 9
:
ÓÓ: ;
	Attribute
ÓÓ< E
{
ÔÔ 
public
 /
!AspMvcViewLocationFormatAttribute
 0
(
0 1
[
1 2
NotNull
2 9
]
9 :
string
; A
format
B H
)
H I
{
ÒÒ 	
Format
ÚÚ 
=
ÚÚ 
format
ÚÚ 
;
ÚÚ 
}
ÛÛ 	
[
ıı 	
NotNull
ıı	 
]
ıı 
public
ıı 
string
ıı 
Format
ıı  &
{
ıı' (
get
ıı) ,
;
ıı, -
}
ıı. /
}
ˆˆ 
[
˛˛ 
AttributeUsage
˛˛ 
(
˛˛ 
AttributeTargets
˛˛ $
.
˛˛$ %
	Parameter
˛˛% .
|
˛˛/ 0
AttributeTargets
˛˛1 A
.
˛˛A B
Method
˛˛B H
)
˛˛H I
]
˛˛I J
public
ˇˇ 

sealed
ˇˇ 
class
ˇˇ #
AspMvcActionAttribute
ˇˇ -
:
ˇˇ. /
	Attribute
ˇˇ0 9
{
ÄÄ 
public
ÅÅ #
AspMvcActionAttribute
ÅÅ $
(
ÅÅ$ %
)
ÅÅ% &
{
ÇÇ 	
}
ÉÉ 	
public
ÖÖ #
AspMvcActionAttribute
ÖÖ $
(
ÖÖ$ %
[
ÖÖ% &
NotNull
ÖÖ& -
]
ÖÖ- .
string
ÖÖ/ 5
anonymousProperty
ÖÖ6 G
)
ÖÖG H
{
ÜÜ 	
AnonymousProperty
áá 
=
áá 
anonymousProperty
áá  1
;
áá1 2
}
àà 	
[
ää 	
	CanBeNull
ää	 
]
ää 
public
ää 
string
ää !
AnonymousProperty
ää" 3
{
ää4 5
get
ää6 9
;
ää9 :
}
ää; <
}
ãã 
[
íí 
AttributeUsage
íí 
(
íí 
AttributeTargets
íí $
.
íí$ %
	Parameter
íí% .
)
íí. /
]
íí/ 0
public
ìì 

sealed
ìì 
class
ìì !
AspMvcAreaAttribute
ìì +
:
ìì, -
	Attribute
ìì. 7
{
îî 
public
ïï !
AspMvcAreaAttribute
ïï "
(
ïï" #
)
ïï# $
{
ññ 	
}
óó 	
public
ôô !
AspMvcAreaAttribute
ôô "
(
ôô" #
[
ôô# $
NotNull
ôô$ +
]
ôô+ ,
string
ôô- 3
anonymousProperty
ôô4 E
)
ôôE F
{
öö 	
AnonymousProperty
õõ 
=
õõ 
anonymousProperty
õõ  1
;
õõ1 2
}
úú 	
[
ûû 	
	CanBeNull
ûû	 
]
ûû 
public
ûû 
string
ûû !
AnonymousProperty
ûû" 3
{
ûû4 5
get
ûû6 9
;
ûû9 :
}
ûû; <
}
üü 
[
ßß 
AttributeUsage
ßß 
(
ßß 
AttributeTargets
ßß $
.
ßß$ %
	Parameter
ßß% .
|
ßß/ 0
AttributeTargets
ßß1 A
.
ßßA B
Method
ßßB H
)
ßßH I
]
ßßI J
public
®® 

sealed
®® 
class
®® '
AspMvcControllerAttribute
®® 1
:
®®2 3
	Attribute
®®4 =
{
©© 
public
™™ '
AspMvcControllerAttribute
™™ (
(
™™( )
)
™™) *
{
´´ 	
}
¨¨ 	
public
ÆÆ '
AspMvcControllerAttribute
ÆÆ (
(
ÆÆ( )
[
ÆÆ) *
NotNull
ÆÆ* 1
]
ÆÆ1 2
string
ÆÆ3 9
anonymousProperty
ÆÆ: K
)
ÆÆK L
{
ØØ 	
AnonymousProperty
∞∞ 
=
∞∞ 
anonymousProperty
∞∞  1
;
∞∞1 2
}
±± 	
[
≥≥ 	
	CanBeNull
≥≥	 
]
≥≥ 
public
≥≥ 
string
≥≥ !
AnonymousProperty
≥≥" 3
{
≥≥4 5
get
≥≥6 9
;
≥≥9 :
}
≥≥; <
}
¥¥ 
[
∫∫ 
AttributeUsage
∫∫ 
(
∫∫ 
AttributeTargets
∫∫ $
.
∫∫$ %
	Parameter
∫∫% .
)
∫∫. /
]
∫∫/ 0
public
ªª 

sealed
ªª 
class
ªª #
AspMvcMasterAttribute
ªª -
:
ªª. /
	Attribute
ªª0 9
{
ºº 
}
ΩΩ 
[
√√ 
AttributeUsage
√√ 
(
√√ 
AttributeTargets
√√ $
.
√√$ %
	Parameter
√√% .
)
√√. /
]
√√/ 0
public
ƒƒ 

sealed
ƒƒ 
class
ƒƒ &
AspMvcModelTypeAttribute
ƒƒ 0
:
ƒƒ1 2
	Attribute
ƒƒ3 <
{
≈≈ 
}
∆∆ 
[
ŒŒ 
AttributeUsage
ŒŒ 
(
ŒŒ 
AttributeTargets
ŒŒ $
.
ŒŒ$ %
	Parameter
ŒŒ% .
|
ŒŒ/ 0
AttributeTargets
ŒŒ1 A
.
ŒŒA B
Method
ŒŒB H
)
ŒŒH I
]
ŒŒI J
public
œœ 

sealed
œœ 
class
œœ (
AspMvcPartialViewAttribute
œœ 2
:
œœ3 4
	Attribute
œœ5 >
{
–– 
}
—— 
[
÷÷ 
AttributeUsage
÷÷ 
(
÷÷ 
AttributeTargets
÷÷ $
.
÷÷$ %
Class
÷÷% *
|
÷÷+ ,
AttributeTargets
÷÷- =
.
÷÷= >
Method
÷÷> D
)
÷÷D E
]
÷÷E F
public
◊◊ 

sealed
◊◊ 
class
◊◊ .
 AspMvcSuppressViewErrorAttribute
◊◊ 8
:
◊◊9 :
	Attribute
◊◊; D
{
ÿÿ 
}
ŸŸ 
[
‡‡ 
AttributeUsage
‡‡ 
(
‡‡ 
AttributeTargets
‡‡ $
.
‡‡$ %
	Parameter
‡‡% .
)
‡‡. /
]
‡‡/ 0
public
·· 

sealed
·· 
class
·· ,
AspMvcDisplayTemplateAttribute
·· 6
:
··7 8
	Attribute
··9 B
{
‚‚ 
}
„„ 
[
ÍÍ 
AttributeUsage
ÍÍ 
(
ÍÍ 
AttributeTargets
ÍÍ $
.
ÍÍ$ %
	Parameter
ÍÍ% .
)
ÍÍ. /
]
ÍÍ/ 0
public
ÎÎ 

sealed
ÎÎ 
class
ÎÎ +
AspMvcEditorTemplateAttribute
ÎÎ 5
:
ÎÎ6 7
	Attribute
ÎÎ8 A
{
ÏÏ 
}
ÌÌ 
[
ÙÙ 
AttributeUsage
ÙÙ 
(
ÙÙ 
AttributeTargets
ÙÙ $
.
ÙÙ$ %
	Parameter
ÙÙ% .
)
ÙÙ. /
]
ÙÙ/ 0
public
ıı 

sealed
ıı 
class
ıı %
AspMvcTemplateAttribute
ıı /
:
ıı0 1
	Attribute
ıı2 ;
{
ˆˆ 
}
˜˜ 
[
ˇˇ 
AttributeUsage
ˇˇ 
(
ˇˇ 
AttributeTargets
ˇˇ $
.
ˇˇ$ %
	Parameter
ˇˇ% .
|
ˇˇ/ 0
AttributeTargets
ˇˇ1 A
.
ˇˇA B
Method
ˇˇB H
)
ˇˇH I
]
ˇˇI J
public
ÄÄ 

sealed
ÄÄ 
class
ÄÄ !
AspMvcViewAttribute
ÄÄ +
:
ÄÄ, -
	Attribute
ÄÄ. 7
{
ÅÅ 
}
ÇÇ 
[
àà 
AttributeUsage
àà 
(
àà 
AttributeTargets
àà $
.
àà$ %
	Parameter
àà% .
)
àà. /
]
àà/ 0
public
ââ 

sealed
ââ 
class
ââ *
AspMvcViewComponentAttribute
ââ 4
:
ââ5 6
	Attribute
ââ7 @
{
ää 
}
ãã 
[
ëë 
AttributeUsage
ëë 
(
ëë 
AttributeTargets
ëë $
.
ëë$ %
	Parameter
ëë% .
|
ëë/ 0
AttributeTargets
ëë1 A
.
ëëA B
Method
ëëB H
)
ëëH I
]
ëëI J
public
íí 

sealed
íí 
class
íí .
 AspMvcViewComponentViewAttribute
íí 8
:
íí9 :
	Attribute
íí; D
{
ìì 
}
îî 
[
££ 
AttributeUsage
££ 
(
££ 
AttributeTargets
££ $
.
££$ %
	Parameter
££% .
|
££/ 0
AttributeTargets
££1 A
.
££A B
Property
££B J
)
££J K
]
££K L
public
§§ 

sealed
§§ 
class
§§ +
AspMvcActionSelectorAttribute
§§ 5
:
§§6 7
	Attribute
§§8 A
{
•• 
}
¶¶ 
[
®® 
AttributeUsage
®® 
(
®® 
AttributeTargets
®® $
.
®®$ %
	Parameter
®®% .
|
®®/ 0
AttributeTargets
®®1 A
.
®®A B
Property
®®B J
|
®®K L
AttributeTargets
®®M ]
.
®®] ^
Field
®®^ c
)
®®c d
]
®®d e
public
©© 

sealed
©© 
class
©© ,
HtmlElementAttributesAttribute
©© 6
:
©©7 8
	Attribute
©©9 B
{
™™ 
public
´´ ,
HtmlElementAttributesAttribute
´´ -
(
´´- .
)
´´. /
{
¨¨ 	
}
≠≠ 	
public
ØØ ,
HtmlElementAttributesAttribute
ØØ -
(
ØØ- .
[
ØØ. /
NotNull
ØØ/ 6
]
ØØ6 7
string
ØØ8 >
name
ØØ? C
)
ØØC D
{
∞∞ 	
Name
±± 
=
±± 
name
±± 
;
±± 
}
≤≤ 	
[
¥¥ 	
	CanBeNull
¥¥	 
]
¥¥ 
public
¥¥ 
string
¥¥ !
Name
¥¥" &
{
¥¥' (
get
¥¥) ,
;
¥¥, -
}
¥¥. /
}
µµ 
[
∑∑ 
AttributeUsage
∑∑ 
(
∑∑ 
AttributeTargets
∑∑ $
.
∑∑$ %
	Parameter
∑∑% .
|
∑∑/ 0
AttributeTargets
∑∑1 A
.
∑∑A B
Field
∑∑B G
|
∑∑H I
AttributeTargets
∑∑J Z
.
∑∑Z [
Property
∑∑[ c
)
∑∑c d
]
∑∑d e
public
∏∏ 

sealed
∏∏ 
class
∏∏ )
HtmlAttributeValueAttribute
∏∏ 3
:
∏∏4 5
	Attribute
∏∏6 ?
{
ππ 
public
∫∫ )
HtmlAttributeValueAttribute
∫∫ *
(
∫∫* +
[
∫∫+ ,
NotNull
∫∫, 3
]
∫∫3 4
string
∫∫5 ;
name
∫∫< @
)
∫∫@ A
{
ªª 	
Name
ºº 
=
ºº 
name
ºº 
;
ºº 
}
ΩΩ 	
[
øø 	
NotNull
øø	 
]
øø 
public
øø 
string
øø 
Name
øø  $
{
øø% &
get
øø' *
;
øø* +
}
øø, -
}
¿¿ 
[
«« 
AttributeUsage
«« 
(
«« 
AttributeTargets
«« $
.
««$ %
	Parameter
««% .
|
««/ 0
AttributeTargets
««1 A
.
««A B
Method
««B H
)
««H I
]
««I J
public
»» 

sealed
»» 
class
»» #
RazorSectionAttribute
»» -
:
»». /
	Attribute
»»0 9
{
…… 
}
   
[
–– 
AttributeUsage
–– 
(
–– 
AttributeTargets
–– $
.
––$ %
Method
––% +
|
––, -
AttributeTargets
––. >
.
––> ?
Constructor
––? J
|
––K L
AttributeTargets
––M ]
.
––] ^
Property
––^ f
)
––f g
]
––g h
public
—— 

sealed
—— 
class
—— '
CollectionAccessAttribute
—— 1
:
——2 3
	Attribute
——4 =
{
““ 
public
”” '
CollectionAccessAttribute
”” (
(
””( )"
CollectionAccessType
””) ="
collectionAccessType
””> R
)
””R S
{
‘‘ 	"
CollectionAccessType
’’  
=
’’! ""
collectionAccessType
’’# 7
;
’’7 8
}
÷÷ 	
public
ÿÿ "
CollectionAccessType
ÿÿ #"
CollectionAccessType
ÿÿ$ 8
{
ÿÿ9 :
get
ÿÿ; >
;
ÿÿ> ?
}
ÿÿ@ A
}
ŸŸ 
[
€€ 
Flags
€€ 

]
€€
 
public
‹‹ 

enum
‹‹ "
CollectionAccessType
‹‹ $
{
›› 
None
ﬂﬂ 
=
ﬂﬂ 
$num
ﬂﬂ 
,
ﬂﬂ 
Read
‚‚ 
=
‚‚ 
$num
‚‚ 
,
‚‚ #
ModifyExistingContent
ÂÂ 
=
ÂÂ 
$num
ÂÂ  !
,
ÂÂ! "
UpdatedContent
ËË 
=
ËË #
ModifyExistingContent
ËË .
|
ËË/ 0
$num
ËË1 2
}
ÈÈ 
[
 
AttributeUsage
 
(
 
AttributeTargets
 $
.
$ %
Method
% +
)
+ ,
]
, -
public
ÒÒ 

sealed
ÒÒ 
class
ÒÒ &
AssertionMethodAttribute
ÒÒ 0
:
ÒÒ1 2
	Attribute
ÒÒ3 <
{
ÚÚ 
}
ÛÛ 
[
˙˙ 
AttributeUsage
˙˙ 
(
˙˙ 
AttributeTargets
˙˙ $
.
˙˙$ %
	Parameter
˙˙% .
)
˙˙. /
]
˙˙/ 0
public
˚˚ 

sealed
˚˚ 
class
˚˚ )
AssertionConditionAttribute
˚˚ 3
:
˚˚4 5
	Attribute
˚˚6 ?
{
¸¸ 
public
˝˝ )
AssertionConditionAttribute
˝˝ *
(
˝˝* +$
AssertionConditionType
˝˝+ A
conditionType
˝˝B O
)
˝˝O P
{
˛˛ 	
ConditionType
ˇˇ 
=
ˇˇ 
conditionType
ˇˇ )
;
ˇˇ) *
}
ÄÄ 	
public
ÇÇ $
AssertionConditionType
ÇÇ %
ConditionType
ÇÇ& 3
{
ÇÇ4 5
get
ÇÇ6 9
;
ÇÇ9 :
}
ÇÇ; <
}
ÉÉ 
public
ââ 

enum
ââ $
AssertionConditionType
ââ &
{
ää 
IS_TRUE
åå 
=
åå 
$num
åå 
,
åå 
IS_FALSE
èè 
=
èè 
$num
èè 
,
èè 
IS_NULL
íí 
=
íí 
$num
íí 
,
íí 
IS_NOT_NULL
ïï 
=
ïï 
$num
ïï 
}
ññ 
[
úú 
Obsolete
úú 
(
úú 
$str
úú ;
)
úú; <
]
úú< =
[
ùù 
AttributeUsage
ùù 
(
ùù 
AttributeTargets
ùù $
.
ùù$ %
Method
ùù% +
)
ùù+ ,
]
ùù, -
public
ûû 

sealed
ûû 
class
ûû (
TerminatesProgramAttribute
ûû 2
:
ûû3 4
	Attribute
ûû5 >
{
üü 
}
†† 
[
ßß 
AttributeUsage
ßß 
(
ßß 
AttributeTargets
ßß $
.
ßß$ %
Method
ßß% +
)
ßß+ ,
]
ßß, -
public
®® 

sealed
®® 
class
®® !
LinqTunnelAttribute
®® +
:
®®, -
	Attribute
®®. 7
{
©© 
}
™™ 
[
ØØ 
AttributeUsage
ØØ 
(
ØØ 
AttributeTargets
ØØ $
.
ØØ$ %
	Parameter
ØØ% .
)
ØØ. /
]
ØØ/ 0
public
∞∞ 

sealed
∞∞ 
class
∞∞ $
NoEnumerationAttribute
∞∞ .
:
∞∞/ 0
	Attribute
∞∞1 :
{
±± 
}
≤≤ 
[
∑∑ 
AttributeUsage
∑∑ 
(
∑∑ 
AttributeTargets
∑∑ $
.
∑∑$ %
	Parameter
∑∑% .
)
∑∑. /
]
∑∑/ 0
public
∏∏ 

sealed
∏∏ 
class
∏∏ #
RegexPatternAttribute
∏∏ -
:
∏∏. /
	Attribute
∏∏0 9
{
ππ 
}
∫∫ 
[
¬¬ 
AttributeUsage
¬¬ 
(
¬¬ 
AttributeTargets
√√ 
.
√√ 
Class
√√ 
|
√√  
AttributeTargets
√√! 1
.
√√1 2
	Interface
√√2 ;
|
√√< =
AttributeTargets
√√> N
.
√√N O
Struct
√√O U
|
√√V W
AttributeTargets
√√X h
.
√√h i
Enum
√√i m
)
√√m n
]
√√n o
public
ƒƒ 

sealed
ƒƒ 
class
ƒƒ  
NoReorderAttribute
ƒƒ *
:
ƒƒ+ ,
	Attribute
ƒƒ- 6
{
≈≈ 
}
∆∆ 
[
ÃÃ 
AttributeUsage
ÃÃ 
(
ÃÃ 
AttributeTargets
ÃÃ $
.
ÃÃ$ %
Class
ÃÃ% *
)
ÃÃ* +
]
ÃÃ+ ,
public
ÕÕ 

sealed
ÕÕ 
class
ÕÕ '
XamlItemsControlAttribute
ÕÕ 1
:
ÕÕ2 3
	Attribute
ÕÕ4 =
{
ŒŒ 
}
œœ 
[
⁄⁄ 
AttributeUsage
⁄⁄ 
(
⁄⁄ 
AttributeTargets
⁄⁄ $
.
⁄⁄$ %
Property
⁄⁄% -
)
⁄⁄- .
]
⁄⁄. /
public
€€ 

sealed
€€ 
class
€€ 4
&XamlItemBindingOfItemsControlAttribute
€€ >
:
€€? @
	Attribute
€€A J
{
‹‹ 
}
›› 
[
ﬂﬂ 
AttributeUsage
ﬂﬂ 
(
ﬂﬂ 
AttributeTargets
ﬂﬂ $
.
ﬂﬂ$ %
Class
ﬂﬂ% *
,
ﬂﬂ* +
AllowMultiple
ﬂﬂ, 9
=
ﬂﬂ: ;
true
ﬂﬂ< @
)
ﬂﬂ@ A
]
ﬂﬂA B
public
‡‡ 

sealed
‡‡ 
class
‡‡ *
AspChildControlTypeAttribute
‡‡ 4
:
‡‡5 6
	Attribute
‡‡7 @
{
·· 
public
‚‚ *
AspChildControlTypeAttribute
‚‚ +
(
‚‚+ ,
[
‚‚, -
NotNull
‚‚- 4
]
‚‚4 5
string
‚‚6 <
tagName
‚‚= D
,
‚‚D E
[
‚‚F G
NotNull
‚‚G N
]
‚‚N O
Type
‚‚P T
controlType
‚‚U `
)
‚‚` a
{
„„ 	
TagName
‰‰ 
=
‰‰ 
tagName
‰‰ 
;
‰‰ 
ControlType
ÂÂ 
=
ÂÂ 
controlType
ÂÂ %
;
ÂÂ% &
}
ÊÊ 	
[
ËË 	
NotNull
ËË	 
]
ËË 
public
ËË 
string
ËË 
TagName
ËË  '
{
ËË( )
get
ËË* -
;
ËË- .
}
ËË/ 0
[
ÍÍ 	
NotNull
ÍÍ	 
]
ÍÍ 
public
ÍÍ 
Type
ÍÍ 
ControlType
ÍÍ )
{
ÍÍ* +
get
ÍÍ, /
;
ÍÍ/ 0
}
ÍÍ1 2
}
ÎÎ 
[
ÌÌ 
AttributeUsage
ÌÌ 
(
ÌÌ 
AttributeTargets
ÌÌ $
.
ÌÌ$ %
Property
ÌÌ% -
|
ÌÌ. /
AttributeTargets
ÌÌ0 @
.
ÌÌ@ A
Method
ÌÌA G
)
ÌÌG H
]
ÌÌH I
public
ÓÓ 

sealed
ÓÓ 
class
ÓÓ #
AspDataFieldAttribute
ÓÓ -
:
ÓÓ. /
	Attribute
ÓÓ0 9
{
ÔÔ 
}
 
[
ÚÚ 
AttributeUsage
ÚÚ 
(
ÚÚ 
AttributeTargets
ÚÚ $
.
ÚÚ$ %
Property
ÚÚ% -
|
ÚÚ. /
AttributeTargets
ÚÚ0 @
.
ÚÚ@ A
Method
ÚÚA G
)
ÚÚG H
]
ÚÚH I
public
ÛÛ 

sealed
ÛÛ 
class
ÛÛ $
AspDataFieldsAttribute
ÛÛ .
:
ÛÛ/ 0
	Attribute
ÛÛ1 :
{
ÙÙ 
}
ıı 
[
˜˜ 
AttributeUsage
˜˜ 
(
˜˜ 
AttributeTargets
˜˜ $
.
˜˜$ %
Property
˜˜% -
)
˜˜- .
]
˜˜. /
public
¯¯ 

sealed
¯¯ 
class
¯¯ (
AspMethodPropertyAttribute
¯¯ 2
:
¯¯3 4
	Attribute
¯¯5 >
{
˘˘ 
}
˙˙ 
[
¸¸ 
AttributeUsage
¸¸ 
(
¸¸ 
AttributeTargets
¸¸ $
.
¸¸$ %
Class
¸¸% *
,
¸¸* +
AllowMultiple
¸¸, 9
=
¸¸: ;
true
¸¸< @
)
¸¸@ A
]
¸¸A B
public
˝˝ 

sealed
˝˝ 
class
˝˝ +
AspRequiredAttributeAttribute
˝˝ 5
:
˝˝6 7
	Attribute
˝˝8 A
{
˛˛ 
public
ˇˇ +
AspRequiredAttributeAttribute
ˇˇ ,
(
ˇˇ, -
[
ˇˇ- .
NotNull
ˇˇ. 5
]
ˇˇ5 6
string
ˇˇ7 =
	attribute
ˇˇ> G
)
ˇˇG H
{
Ä	Ä	 	
	Attribute
Å	Å	 
=
Å	Å	 
	attribute
Å	Å	 !
;
Å	Å	! "
}
Ç	Ç	 	
[
Ñ	Ñ	 	
NotNull
Ñ	Ñ		 
]
Ñ	Ñ	 
public
Ñ	Ñ	 
string
Ñ	Ñ	 
	Attribute
Ñ	Ñ	  )
{
Ñ	Ñ	* +
get
Ñ	Ñ	, /
;
Ñ	Ñ	/ 0
}
Ñ	Ñ	1 2
}
Ö	Ö	 
[
á	á	 
AttributeUsage
á	á	 
(
á	á	 
AttributeTargets
á	á	 $
.
á	á	$ %
Property
á	á	% -
)
á	á	- .
]
á	á	. /
public
à	à	 

sealed
à	à	 
class
à	à	 &
AspTypePropertyAttribute
à	à	 0
:
à	à	1 2
	Attribute
à	à	3 <
{
â	â	 
public
ä	ä	 &
AspTypePropertyAttribute
ä	ä	 '
(
ä	ä	' (
bool
ä	ä	( ,)
createConstructorReferences
ä	ä	- H
)
ä	ä	H I
{
ã	ã	 	)
CreateConstructorReferences
å	å	 '
=
å	å	( ))
createConstructorReferences
å	å	* E
;
å	å	E F
}
ç	ç	 	
public
è	è	 
bool
è	è	 )
CreateConstructorReferences
è	è	 /
{
è	è	0 1
get
è	è	2 5
;
è	è	5 6
}
è	è	7 8
}
ê	ê	 
[
í	í	 
AttributeUsage
í	í	 
(
í	í	 
AttributeTargets
í	í	 $
.
í	í	$ %
Assembly
í	í	% -
,
í	í	- .
AllowMultiple
í	í	/ <
=
í	í	= >
true
í	í	? C
)
í	í	C D
]
í	í	D E
public
ì	ì	 

sealed
ì	ì	 
class
ì	ì	 +
RazorImportNamespaceAttribute
ì	ì	 5
:
ì	ì	6 7
	Attribute
ì	ì	8 A
{
î	î	 
public
ï	ï	 +
RazorImportNamespaceAttribute
ï	ï	 ,
(
ï	ï	, -
[
ï	ï	- .
NotNull
ï	ï	. 5
]
ï	ï	5 6
string
ï	ï	7 =
name
ï	ï	> B
)
ï	ï	B C
{
ñ	ñ	 	
Name
ó	ó	 
=
ó	ó	 
name
ó	ó	 
;
ó	ó	 
}
ò	ò	 	
[
ö	ö	 	
NotNull
ö	ö		 
]
ö	ö	 
public
ö	ö	 
string
ö	ö	 
Name
ö	ö	  $
{
ö	ö	% &
get
ö	ö	' *
;
ö	ö	* +
}
ö	ö	, -
}
õ	õ	 
[
ù	ù	 
AttributeUsage
ù	ù	 
(
ù	ù	 
AttributeTargets
ù	ù	 $
.
ù	ù	$ %
Assembly
ù	ù	% -
,
ù	ù	- .
AllowMultiple
ù	ù	/ <
=
ù	ù	= >
true
ù	ù	? C
)
ù	ù	C D
]
ù	ù	D E
public
û	û	 

sealed
û	û	 
class
û	û	 %
RazorInjectionAttribute
û	û	 /
:
û	û	0 1
	Attribute
û	û	2 ;
{
ü	ü	 
public
†	†	 %
RazorInjectionAttribute
†	†	 &
(
†	†	& '
[
†	†	' (
NotNull
†	†	( /
]
†	†	/ 0
string
†	†	1 7
type
†	†	8 <
,
†	†	< =
[
†	†	> ?
NotNull
†	†	? F
]
†	†	F G
string
†	†	H N
	fieldName
†	†	O X
)
†	†	X Y
{
°	°	 	
Type
¢	¢	 
=
¢	¢	 
type
¢	¢	 
;
¢	¢	 
	FieldName
£	£	 
=
£	£	 
	fieldName
£	£	 !
;
£	£	! "
}
§	§	 	
[
¶	¶	 	
NotNull
¶	¶		 
]
¶	¶	 
public
¶	¶	 
string
¶	¶	 
Type
¶	¶	  $
{
¶	¶	% &
get
¶	¶	' *
;
¶	¶	* +
}
¶	¶	, -
[
®	®	 	
NotNull
®	®		 
]
®	®	 
public
®	®	 
string
®	®	 
	FieldName
®	®	  )
{
®	®	* +
get
®	®	, /
;
®	®	/ 0
}
®	®	1 2
}
©	©	 
[
´	´	 
AttributeUsage
´	´	 
(
´	´	 
AttributeTargets
´	´	 $
.
´	´	$ %
Assembly
´	´	% -
,
´	´	- .
AllowMultiple
´	´	/ <
=
´	´	= >
true
´	´	? C
)
´	´	C D
]
´	´	D E
public
¨	¨	 

sealed
¨	¨	 
class
¨	¨	 %
RazorDirectiveAttribute
¨	¨	 /
:
¨	¨	0 1
	Attribute
¨	¨	2 ;
{
≠	≠	 
public
Æ	Æ	 %
RazorDirectiveAttribute
Æ	Æ	 &
(
Æ	Æ	& '
[
Æ	Æ	' (
NotNull
Æ	Æ	( /
]
Æ	Æ	/ 0
string
Æ	Æ	1 7
	directive
Æ	Æ	8 A
)
Æ	Æ	A B
{
Ø	Ø	 	
	Directive
∞	∞	 
=
∞	∞	 
	directive
∞	∞	 !
;
∞	∞	! "
}
±	±	 	
[
≥	≥	 	
NotNull
≥	≥		 
]
≥	≥	 
public
≥	≥	 
string
≥	≥	 
	Directive
≥	≥	  )
{
≥	≥	* +
get
≥	≥	, /
;
≥	≥	/ 0
}
≥	≥	1 2
}
¥	¥	 
[
∂	∂	 
AttributeUsage
∂	∂	 
(
∂	∂	 
AttributeTargets
∂	∂	 $
.
∂	∂	$ %
Method
∂	∂	% +
)
∂	∂	+ ,
]
∂	∂	, -
public
∑	∑	 

sealed
∑	∑	 
class
∑	∑	 (
RazorHelperCommonAttribute
∑	∑	 2
:
∑	∑	3 4
	Attribute
∑	∑	5 >
{
∏	∏	 
}
π	π	 
[
ª	ª	 
AttributeUsage
ª	ª	 
(
ª	ª	 
AttributeTargets
ª	ª	 $
.
ª	ª	$ %
Property
ª	ª	% -
)
ª	ª	- .
]
ª	ª	. /
public
º	º	 

sealed
º	º	 
class
º	º	 "
RazorLayoutAttribute
º	º	 ,
:
º	º	- .
	Attribute
º	º	/ 8
{
Ω	Ω	 
}
æ	æ	 
[
¿	¿	 
AttributeUsage
¿	¿	 
(
¿	¿	 
AttributeTargets
¿	¿	 $
.
¿	¿	$ %
Method
¿	¿	% +
)
¿	¿	+ ,
]
¿	¿	, -
public
¡	¡	 

sealed
¡	¡	 
class
¡	¡	 .
 RazorWriteLiteralMethodAttribute
¡	¡	 8
:
¡	¡	9 :
	Attribute
¡	¡	; D
{
¬	¬	 
}
√	√	 
[
≈	≈	 
AttributeUsage
≈	≈	 
(
≈	≈	 
AttributeTargets
≈	≈	 $
.
≈	≈	$ %
Method
≈	≈	% +
)
≈	≈	+ ,
]
≈	≈	, -
public
∆	∆	 

sealed
∆	∆	 
class
∆	∆	 '
RazorWriteMethodAttribute
∆	∆	 1
:
∆	∆	2 3
	Attribute
∆	∆	4 =
{
«	«	 
}
»	»	 
[
 	 	 
AttributeUsage
 	 	 
(
 	 	 
AttributeTargets
 	 	 $
.
 	 	$ %
	Parameter
 	 	% .
)
 	 	. /
]
 	 	/ 0
public
À	À	 

sealed
À	À	 
class
À	À	 0
"RazorWriteMethodParameterAttribute
À	À	 :
:
À	À	; <
	Attribute
À	À	= F
{
Ã	Ã	 
}
Õ	Õ	 
}Œ	Œ	 È
mD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.Wpf\ObservableCollectionEx.cs
	namespace 	
Titanium
 
. 
Web 
. 
Proxy 
. 
Examples %
.% &
Wpf& )
{ 
public 

class "
ObservableCollectionEx '
<' (
T( )
>) *
:+ , 
ObservableCollection- A
<A B
TB C
>C D
{ 
private 
bool "
notificationSuppressed +
;+ ,
private		 
bool		  
suppressNotification		 )
;		) *
public 
bool  
SuppressNotification (
{ 	
get 
=>  
suppressNotification '
;' (
set 
{  
suppressNotification $
=% &
value' ,
;, -
if 
(  
suppressNotification (
==) +
false, 1
&&2 4"
notificationSuppressed5 K
)K L
{ 
OnCollectionChanged '
(' (
new( +,
 NotifyCollectionChangedEventArgs, L
(L M)
NotifyCollectionChangedActionM j
.j k
Resetk p
)p q
)q r
;r s"
notificationSuppressed *
=+ ,
false- 2
;2 3
} 
} 
} 	
	protected 
override 
void 
OnCollectionChanged  3
(3 4,
 NotifyCollectionChangedEventArgs4 T
eU V
)V W
{ 	
if 
(  
SuppressNotification $
)$ %
{ "
notificationSuppressed &
=' (
true) -
;- .
return 
; 
} 
base!! 
.!! 
OnCollectionChanged!! $
(!!$ %
e!!% &
)!!& '
;!!' (
}"" 	
}## 
}$$ ä¢
fD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.Wpf\MainWindow.xaml.cs
	namespace 	
Titanium
 
. 
Web 
. 
Proxy 
. 
Examples %
.% &
Wpf& )
{ 
public 

partial 
class 

MainWindow #
:$ %
Window& ,
{ 
public 
static 
readonly 
DependencyProperty 1)
ClientConnectionCountProperty2 O
=P Q
DependencyPropertyR d
.d e
Registere m
(m n
nameof 
( !
ClientConnectionCount (
)( )
,) *
typeof+ 1
(1 2
int2 5
)5 6
,6 7
typeof8 >
(> ?

MainWindow? I
)I J
,J K
newL O
PropertyMetadataP `
(` a
defaulta h
(h i
inti l
)l m
)m n
)n o
;o p
public 
static 
readonly 
DependencyProperty 1)
ServerConnectionCountProperty2 O
=P Q
DependencyPropertyR d
.d e
Registere m
(m n
nameof 
( !
ServerConnectionCount (
)( )
,) *
typeof+ 1
(1 2
int2 5
)5 6
,6 7
typeof8 >
(> ?

MainWindow? I
)I J
,J K
newL O
PropertyMetadataP `
(` a
defaulta h
(h i
inti l
)l m
)m n
)n o
;o p
private 
readonly 
ProxyServer $
proxyServer% 0
;0 1
private   
readonly   

Dictionary   #
<  # $
HttpWebClient  $ 1
,  1 2
SessionListItem  3 B
>  B C
sessionDictionary  D U
=  V W
new!! 

Dictionary!! 
<!! 
HttpWebClient!! (
,!!( )
SessionListItem!!* 9
>!!9 :
(!!: ;
)!!; <
;!!< =
private## 
int## 
lastSessionNumber## %
;##% &
private$$ 
SessionListItem$$ 
selectedSession$$  /
;$$/ 0
public&& 

MainWindow&& 
(&& 
)&& 
{'' 	
proxyServer(( 
=(( 
new(( 
ProxyServer(( )
((() *
)((* +
;((+ ,
var))  
certificateDirectory)) $
=))% &
Path))' +
.))+ ,
Combine)), 3
())3 4
Environment** 
.** 
GetFolderPath** )
(**) *
Environment*** 5
.**5 6
SpecialFolder**6 C
.**C D 
LocalApplicationData**D X
)**X Y
,**Y Z
$str++ $
)++$ %
;++% &
	Directory,, 
.,, 
CreateDirectory,, %
(,,% & 
certificateDirectory,,& :
),,: ;
;,,; <
proxyServer-- 
.-- 
CertificateManager-- *
.--* +
PfxFilePath--+ 6
=--7 8
Path--9 =
.--= >
Combine--> E
(--E F 
certificateDirectory--F Z
,--Z [
$str--\ j
)--j k
;--k l
proxyServer@@ 
.@@ $
ForwardToUpstreamGateway@@ 0
=@@1 2
true@@3 7
;@@7 8
varNN 
explicitEndPointNN  
=NN! "
newNN# &!
ExplicitProxyEndPointNN' <
(NN< =
	IPAddressNN= F
.NNF G
AnyNNG J
,NNJ K
$numNNL P
)NNP Q
;NNQ R
proxyServerPP 
.PP 
AddEndPointPP #
(PP# $
explicitEndPointPP$ 4
)PP4 5
;PP5 6
proxyServerbb 
.bb 
BeforeRequestbb %
+=bb& (%
ProxyServer_BeforeRequestbb) B
;bbB C
proxyServercc 
.cc 
BeforeResponsecc &
+=cc' )&
ProxyServer_BeforeResponsecc* D
;ccD E
proxyServerdd 
.dd 
AfterResponsedd %
+=dd& (%
ProxyServer_AfterResponsedd) B
;ddB C
explicitEndPointee 
.ee &
BeforeTunnelConnectRequestee 7
+=ee8 :2
&ProxyServer_BeforeTunnelConnectRequestee; a
;eea b
explicitEndPointff 
.ff '
BeforeTunnelConnectResponseff 8
+=ff9 ;3
'ProxyServer_BeforeTunnelConnectResponseff< c
;ffc d
proxyServergg 
.gg (
ClientConnectionCountChangedgg 4
+=gg5 7
delegategg8 @
{hh 

Dispatcherii 
.ii 
Invokeii !
(ii! "
(ii" #
)ii# $
=>ii% '
{ii( )!
ClientConnectionCountii* ?
=ii@ A
proxyServeriiB M
.iiM N!
ClientConnectionCountiiN c
;iic d
}iie f
)iif g
;iig h
}jj 
;jj 
proxyServerkk 
.kk (
ServerConnectionCountChangedkk 4
+=kk5 7
delegatekk8 @
{ll 

Dispatchermm 
.mm 
Invokemm !
(mm! "
(mm" #
)mm# $
=>mm% '
{mm( )!
ServerConnectionCountmm* ?
=mm@ A
proxyServermmB M
.mmM N!
ServerConnectionCountmmN c
;mmc d
}mme f
)mmf g
;mmg h
}nn 
;nn 
proxyServeroo 
.oo 
Startoo 
(oo 
)oo 
;oo  
proxyServerqq 
.qq 
SetAsSystemProxyqq (
(qq( )
explicitEndPointqq) 9
,qq9 :
ProxyProtocolTypeqq; L
.qqL M
AllHttpqqM T
,qqT U
newqqV Y
SystemProxySettingsqqZ m
{rr 
ProxyLoopbacktt 
=tt 
truett  $
}uu 
)uu 
;uu 
InitializeComponentww 
(ww  
)ww  !
;ww! "
}xx 	
publiczz "
ObservableCollectionExzz %
<zz% &
SessionListItemzz& 5
>zz5 6
Sessionszz7 ?
{zz@ A
getzzB E
;zzE F
}zzG H
=zzI J
new{{ "
ObservableCollectionEx{{ &
<{{& '
SessionListItem{{' 6
>{{6 7
({{7 8
){{8 9
;{{9 :
public}} 
SessionListItem}} 
SelectedSession}} .
{~~ 	
get 
=> 
selectedSession "
;" #
set
ÄÄ 
{
ÅÅ 
if
ÇÇ 
(
ÇÇ 
value
ÇÇ 
!=
ÇÇ 
selectedSession
ÇÇ ,
)
ÇÇ, -
{
ÉÉ 
selectedSession
ÑÑ #
=
ÑÑ$ %
value
ÑÑ& +
;
ÑÑ+ ,$
SelectedSessionChanged
ÖÖ *
(
ÖÖ* +
)
ÖÖ+ ,
;
ÖÖ, -
}
ÜÜ 
}
áá 
}
àà 	
public
ää 
int
ää #
ClientConnectionCount
ää (
{
ãã 	
get
åå 
=>
åå 
(
åå 
int
åå 
)
åå 
GetValue
åå  
(
åå  !+
ClientConnectionCountProperty
åå! >
)
åå> ?
;
åå? @
set
çç 
=>
çç 
SetValue
çç 
(
çç +
ClientConnectionCountProperty
çç 9
,
çç9 :
value
çç; @
)
çç@ A
;
ççA B
}
éé 	
public
êê 
int
êê #
ServerConnectionCount
êê (
{
ëë 	
get
íí 
=>
íí 
(
íí 
int
íí 
)
íí 
GetValue
íí  
(
íí  !+
ServerConnectionCountProperty
íí! >
)
íí> ?
;
íí? @
set
ìì 
=>
ìì 
SetValue
ìì 
(
ìì +
ServerConnectionCountProperty
ìì 9
,
ìì9 :
value
ìì; @
)
ìì@ A
;
ììA B
}
îî 	
private
ññ 
async
ññ 
Task
ññ 4
&ProxyServer_BeforeTunnelConnectRequest
ññ A
(
ññA B
object
ññB H
sender
ññI O
,
ññO P+
TunnelConnectSessionEventArgs
ññQ n
e
ñño p
)
ññp q
{
óó 	
var
òò 
hostname
òò 
=
òò 
e
òò 
.
òò 

HttpClient
òò '
.
òò' (
Request
òò( /
.
òò/ 0

RequestUri
òò0 :
.
òò: ;
Host
òò; ?
;
òò? @
if
ôô 
(
ôô 
hostname
ôô 
.
ôô 
EndsWith
ôô !
(
ôô! "
$str
ôô" -
)
ôô- .
)
ôô. /
e
ôô0 1
.
ôô1 2

DecryptSsl
ôô2 <
=
ôô= >
false
ôô? D
;
ôôD E
await
õõ 

Dispatcher
õõ 
.
õõ 
InvokeAsync
õõ (
(
õõ( )
(
õõ) *
)
õõ* +
=>
õõ, .
{
õõ/ 0

AddSession
õõ1 ;
(
õõ; <
e
õõ< =
)
õõ= >
;
õõ> ?
}
õõ@ A
)
õõA B
;
õõB C
}
úú 	
private
ûû 
async
ûû 
Task
ûû 5
'ProxyServer_BeforeTunnelConnectResponse
ûû B
(
ûûB C
object
ûûC I
sender
ûûJ P
,
ûûP Q+
TunnelConnectSessionEventArgs
ûûR o
e
ûûp q
)
ûûq r
{
üü 	
await
†† 

Dispatcher
†† 
.
†† 
InvokeAsync
†† (
(
††( )
(
††) *
)
††* +
=>
††, .
{
°° 
if
¢¢ 
(
¢¢ 
sessionDictionary
¢¢ %
.
¢¢% &
TryGetValue
¢¢& 1
(
¢¢1 2
e
¢¢2 3
.
¢¢3 4

HttpClient
¢¢4 >
,
¢¢> ?
out
¢¢@ C
var
¢¢D G
item
¢¢H L
)
¢¢L M
)
¢¢M N
item
¢¢O S
.
¢¢S T
Update
¢¢T Z
(
¢¢Z [
e
¢¢[ \
)
¢¢\ ]
;
¢¢] ^
}
££ 
)
££ 
;
££ 
}
§§ 	
private
¶¶ 
async
¶¶ 
Task
¶¶ '
ProxyServer_BeforeRequest
¶¶ 4
(
¶¶4 5
object
¶¶5 ;
sender
¶¶< B
,
¶¶B C
SessionEventArgs
¶¶D T
e
¶¶U V
)
¶¶V W
{
ßß 	
SessionListItem
™™ 
item
™™  
=
™™! "
null
™™# '
;
™™' (
await
´´ 

Dispatcher
´´ 
.
´´ 
InvokeAsync
´´ (
(
´´( )
(
´´) *
)
´´* +
=>
´´, .
{
´´/ 0
item
´´1 5
=
´´6 7

AddSession
´´8 B
(
´´B C
e
´´C D
)
´´D E
;
´´E F
}
´´G H
)
´´H I
;
´´I J
if
≠≠ 
(
≠≠ 
e
≠≠ 
.
≠≠ 

HttpClient
≠≠ 
.
≠≠ 
Request
≠≠ $
.
≠≠$ %
HasBody
≠≠% ,
)
≠≠, -
{
ÆÆ 
e
ØØ 
.
ØØ 

HttpClient
ØØ 
.
ØØ 
Request
ØØ $
.
ØØ$ %
KeepBody
ØØ% -
=
ØØ. /
true
ØØ0 4
;
ØØ4 5
await
∞∞ 
e
∞∞ 
.
∞∞ 
GetRequestBody
∞∞ &
(
∞∞& '
)
∞∞' (
;
∞∞( )
if
≤≤ 
(
≤≤ 
item
≤≤ 
==
≤≤ 
SelectedSession
≤≤ +
)
≤≤+ ,
await
≤≤- 2

Dispatcher
≤≤3 =
.
≤≤= >
InvokeAsync
≤≤> I
(
≤≤I J$
SelectedSessionChanged
≤≤J `
)
≤≤` a
;
≤≤a b
}
≥≥ 
}
¥¥ 	
private
∂∂ 
async
∂∂ 
Task
∂∂ (
ProxyServer_BeforeResponse
∂∂ 5
(
∂∂5 6
object
∂∂6 <
sender
∂∂= C
,
∂∂C D
SessionEventArgs
∂∂E U
e
∂∂V W
)
∂∂W X
{
∑∑ 	
SessionListItem
∏∏ 
item
∏∏  
=
∏∏! "
null
∏∏# '
;
∏∏' (
await
ππ 

Dispatcher
ππ 
.
ππ 
InvokeAsync
ππ (
(
ππ( )
(
ππ) *
)
ππ* +
=>
ππ, .
{
∫∫ 
if
ªª 
(
ªª 
sessionDictionary
ªª %
.
ªª% &
TryGetValue
ªª& 1
(
ªª1 2
e
ªª2 3
.
ªª3 4

HttpClient
ªª4 >
,
ªª> ?
out
ªª@ C
item
ªªD H
)
ªªH I
)
ªªI J
item
ªªK O
.
ªªO P
Update
ªªP V
(
ªªV W
e
ªªW X
)
ªªX Y
;
ªªY Z
}
ºº 
)
ºº 
;
ºº 
if
¬¬ 
(
¬¬ 
item
¬¬ 
!=
¬¬ 
null
¬¬ 
)
¬¬ 
if
√√ 
(
√√ 
e
√√ 
.
√√ 

HttpClient
√√  
.
√√  !
Response
√√! )
.
√√) *
HasBody
√√* 1
)
√√1 2
{
ƒƒ 
e
≈≈ 
.
≈≈ 

HttpClient
≈≈  
.
≈≈  !
Response
≈≈! )
.
≈≈) *
KeepBody
≈≈* 2
=
≈≈3 4
true
≈≈5 9
;
≈≈9 :
await
∆∆ 
e
∆∆ 
.
∆∆ 
GetResponseBody
∆∆ +
(
∆∆+ ,
)
∆∆, -
;
∆∆- .
await
»» 

Dispatcher
»» $
.
»»$ %
InvokeAsync
»»% 0
(
»»0 1
(
»»1 2
)
»»2 3
=>
»»4 6
{
»»7 8
item
»»9 =
.
»»= >
Update
»»> D
(
»»D E
e
»»E F
)
»»F G
;
»»G H
}
»»I J
)
»»J K
;
»»K L
if
…… 
(
…… 
item
…… 
==
…… 
SelectedSession
……  /
)
……/ 0
await
……1 6

Dispatcher
……7 A
.
……A B
InvokeAsync
……B M
(
……M N$
SelectedSessionChanged
……N d
)
……d e
;
……e f
}
   
}
ÀÀ 	
private
ÕÕ 
async
ÕÕ 
Task
ÕÕ '
ProxyServer_AfterResponse
ÕÕ 4
(
ÕÕ4 5
object
ÕÕ5 ;
sender
ÕÕ< B
,
ÕÕB C
SessionEventArgs
ÕÕD T
e
ÕÕU V
)
ÕÕV W
{
ŒŒ 	
await
œœ 

Dispatcher
œœ 
.
œœ 
InvokeAsync
œœ (
(
œœ( )
(
œœ) *
)
œœ* +
=>
œœ, .
{
–– 
if
—— 
(
—— 
sessionDictionary
—— %
.
——% &
TryGetValue
——& 1
(
——1 2
e
——2 3
.
——3 4

HttpClient
——4 >
,
——> ?
out
——@ C
var
——D G
item
——H L
)
——L M
)
——M N
item
——O S
.
——S T
	Exception
——T ]
=
——^ _
e
——` a
.
——a b
	Exception
——b k
;
——k l
}
““ 
)
““ 
;
““ 
}
”” 	
private
’’ 
SessionListItem
’’ 

AddSession
’’  *
(
’’* +"
SessionEventArgsBase
’’+ ?
e
’’@ A
)
’’A B
{
÷÷ 	
var
◊◊ 
item
◊◊ 
=
◊◊ #
CreateSessionListItem
◊◊ ,
(
◊◊, -
e
◊◊- .
)
◊◊. /
;
◊◊/ 0
Sessions
ÿÿ 
.
ÿÿ 
Add
ÿÿ 
(
ÿÿ 
item
ÿÿ 
)
ÿÿ 
;
ÿÿ 
sessionDictionary
ŸŸ 
.
ŸŸ 
Add
ŸŸ !
(
ŸŸ! "
e
ŸŸ" #
.
ŸŸ# $

HttpClient
ŸŸ$ .
,
ŸŸ. /
item
ŸŸ0 4
)
ŸŸ4 5
;
ŸŸ5 6
return
⁄⁄ 
item
⁄⁄ 
;
⁄⁄ 
}
€€ 	
private
›› 
SessionListItem
›› #
CreateSessionListItem
››  5
(
››5 6"
SessionEventArgsBase
››6 J
e
››K L
)
››L M
{
ﬁﬁ 	
lastSessionNumber
ﬂﬂ 
++
ﬂﬂ 
;
ﬂﬂ  
var
‡‡ 
isTunnelConnect
‡‡ 
=
‡‡  !
e
‡‡" #
is
‡‡$ &+
TunnelConnectSessionEventArgs
‡‡' D
;
‡‡D E
var
·· 
item
·· 
=
·· 
new
·· 
SessionListItem
·· *
{
‚‚ 
Number
„„ 
=
„„ 
lastSessionNumber
„„ *
,
„„* + 
ClientConnectionId
‰‰ "
=
‰‰# $
e
‰‰% &
.
‰‰& ' 
ClientConnectionId
‰‰' 9
,
‰‰9 : 
ServerConnectionId
ÂÂ "
=
ÂÂ# $
e
ÂÂ% &
.
ÂÂ& ' 
ServerConnectionId
ÂÂ' 9
,
ÂÂ9 :

HttpClient
ÊÊ 
=
ÊÊ 
e
ÊÊ 
.
ÊÊ 

HttpClient
ÊÊ )
,
ÊÊ) *"
ClientRemoteEndPoint
ÁÁ $
=
ÁÁ% &
e
ÁÁ' (
.
ÁÁ( )"
ClientRemoteEndPoint
ÁÁ) =
,
ÁÁ= >!
ClientLocalEndPoint
ËË #
=
ËË$ %
e
ËË& '
.
ËË' (!
ClientLocalEndPoint
ËË( ;
,
ËË; <
IsTunnelConnect
ÈÈ 
=
ÈÈ  !
isTunnelConnect
ÈÈ" 1
}
ÍÍ 
;
ÍÍ 
e
ÌÌ 
.
ÌÌ 
DataReceived
ÌÌ 
+=
ÌÌ 
(
ÌÌ 
sender
ÌÌ %
,
ÌÌ% &
args
ÌÌ' +
)
ÌÌ+ ,
=>
ÌÌ- /
{
ÓÓ 
var
ÔÔ 
session
ÔÔ 
=
ÔÔ 
(
ÔÔ "
SessionEventArgsBase
ÔÔ 3
)
ÔÔ3 4
sender
ÔÔ4 :
;
ÔÔ: ;
if
 
(
 
sessionDictionary
 %
.
% &
TryGetValue
& 1
(
1 2
session
2 9
.
9 :

HttpClient
: D
,
D E
out
F I
var
J M
li
N P
)
P Q
)
Q R
{
ÒÒ 
var
ÚÚ 
connectRequest
ÚÚ &
=
ÚÚ' (
session
ÚÚ) 0
.
ÚÚ0 1

HttpClient
ÚÚ1 ;
.
ÚÚ; <
ConnectRequest
ÚÚ< J
;
ÚÚJ K
var
ÛÛ 

tunnelType
ÛÛ "
=
ÛÛ# $
connectRequest
ÛÛ% 3
?
ÛÛ3 4
.
ÛÛ4 5

TunnelType
ÛÛ5 ?
??
ÛÛ@ B

TunnelType
ÛÛC M
.
ÛÛM N
Unknown
ÛÛN U
;
ÛÛU V
if
ÙÙ 
(
ÙÙ 

tunnelType
ÙÙ "
!=
ÙÙ# %

TunnelType
ÙÙ& 0
.
ÙÙ0 1
Unknown
ÙÙ1 8
)
ÙÙ8 9
li
ÙÙ: <
.
ÙÙ< =
Protocol
ÙÙ= E
=
ÙÙF G 
TunnelTypeToString
ÙÙH Z
(
ÙÙZ [

tunnelType
ÙÙ[ e
)
ÙÙe f
;
ÙÙf g
li
ˆˆ 
.
ˆˆ 
ReceivedDataCount
ˆˆ (
+=
ˆˆ) +
args
ˆˆ, 0
.
ˆˆ0 1
Count
ˆˆ1 6
;
ˆˆ6 7
AppendTransferLog
˘˘ %
(
˘˘% &
session
˘˘& -
.
˘˘- .
GetHashCode
˘˘. 9
(
˘˘9 :
)
˘˘: ;
+
˘˘< =
(
˘˘> ?
isTunnelConnect
˘˘? N
?
˘˘O P
$str
˘˘Q Z
:
˘˘[ \
$str
˘˘] _
)
˘˘_ `
+
˘˘a b
$str
˘˘c n
,
˘˘n o
args
˙˙ 
.
˙˙ 
Buffer
˙˙ #
,
˙˙# $
args
˙˙% )
.
˙˙) *
Offset
˙˙* 0
,
˙˙0 1
args
˙˙2 6
.
˙˙6 7
Count
˙˙7 <
)
˙˙< =
;
˙˙= >
}
˚˚ 
}
¸¸ 
;
¸¸ 
e
˛˛ 
.
˛˛ 
DataSent
˛˛ 
+=
˛˛ 
(
˛˛ 
sender
˛˛ !
,
˛˛! "
args
˛˛# '
)
˛˛' (
=>
˛˛) +
{
ˇˇ 
var
ÄÄ 
session
ÄÄ 
=
ÄÄ 
(
ÄÄ "
SessionEventArgsBase
ÄÄ 3
)
ÄÄ3 4
sender
ÄÄ4 :
;
ÄÄ: ;
if
ÅÅ 
(
ÅÅ 
sessionDictionary
ÅÅ %
.
ÅÅ% &
TryGetValue
ÅÅ& 1
(
ÅÅ1 2
session
ÅÅ2 9
.
ÅÅ9 :

HttpClient
ÅÅ: D
,
ÅÅD E
out
ÅÅF I
var
ÅÅJ M
li
ÅÅN P
)
ÅÅP Q
)
ÅÅQ R
{
ÇÇ 
var
ÉÉ 
connectRequest
ÉÉ &
=
ÉÉ' (
session
ÉÉ) 0
.
ÉÉ0 1

HttpClient
ÉÉ1 ;
.
ÉÉ; <
ConnectRequest
ÉÉ< J
;
ÉÉJ K
var
ÑÑ 

tunnelType
ÑÑ "
=
ÑÑ# $
connectRequest
ÑÑ% 3
?
ÑÑ3 4
.
ÑÑ4 5

TunnelType
ÑÑ5 ?
??
ÑÑ@ B

TunnelType
ÑÑC M
.
ÑÑM N
Unknown
ÑÑN U
;
ÑÑU V
if
ÖÖ 
(
ÖÖ 

tunnelType
ÖÖ "
!=
ÖÖ# %

TunnelType
ÖÖ& 0
.
ÖÖ0 1
Unknown
ÖÖ1 8
)
ÖÖ8 9
li
ÖÖ: <
.
ÖÖ< =
Protocol
ÖÖ= E
=
ÖÖF G 
TunnelTypeToString
ÖÖH Z
(
ÖÖZ [

tunnelType
ÖÖ[ e
)
ÖÖe f
;
ÖÖf g
li
áá 
.
áá 
SentDataCount
áá $
+=
áá% '
args
áá( ,
.
áá, -
Count
áá- 2
;
áá2 3
AppendTransferLog
ää %
(
ää% &
session
ää& -
.
ää- .
GetHashCode
ää. 9
(
ää9 :
)
ää: ;
+
ää< =
(
ää> ?
isTunnelConnect
ää? N
?
ääO P
$str
ääQ Z
:
ää[ \
$str
ää] _
)
ää_ `
+
ääa b
$str
ääc j
,
ääj k
args
ãã 
.
ãã 
Buffer
ãã #
,
ãã# $
args
ãã% )
.
ãã) *
Offset
ãã* 0
,
ãã0 1
args
ãã2 6
.
ãã6 7
Count
ãã7 <
)
ãã< =
;
ãã= >
}
åå 
}
çç 
;
çç 
if
èè 
(
èè 
e
èè 
is
èè +
TunnelConnectSessionEventArgs
èè 2
te
èè3 5
)
èè5 6
{
êê 
te
ëë 
.
ëë #
DecryptedDataReceived
ëë (
+=
ëë) +
(
ëë, -
sender
ëë- 3
,
ëë3 4
args
ëë5 9
)
ëë9 :
=>
ëë; =
{
íí 
var
ìì 
session
ìì 
=
ìì  !
(
ìì" #"
SessionEventArgsBase
ìì# 7
)
ìì7 8
sender
ìì8 >
;
ìì> ?
AppendTransferLog
ññ %
(
ññ% &
session
ññ& -
.
ññ- .
GetHashCode
ññ. 9
(
ññ9 :
)
ññ: ;
+
ññ< =
$str
ññ> S
,
ññS T
args
ññU Y
.
ññY Z
Buffer
ññZ `
,
ññ` a
args
ññb f
.
ññf g
Offset
ññg m
,
ññm n
args
óó 
.
óó 
Count
óó "
)
óó" #
;
óó# $
}
òò 
;
òò 
te
öö 
.
öö 
DecryptedDataSent
öö $
+=
öö% '
(
öö( )
sender
öö) /
,
öö/ 0
args
öö1 5
)
öö5 6
=>
öö7 9
{
õõ 
var
úú 
session
úú 
=
úú  !
(
úú" #"
SessionEventArgsBase
úú# 7
)
úú7 8
sender
úú8 >
;
úú> ?
AppendTransferLog
üü %
(
üü% &
session
üü& -
.
üü- .
GetHashCode
üü. 9
(
üü9 :
)
üü: ;
+
üü< =
$str
üü> O
,
üüO P
args
üüQ U
.
üüU V
Buffer
üüV \
,
üü\ ]
args
üü^ b
.
üüb c
Offset
üüc i
,
üüi j
args
üük o
.
üüo p
Count
üüp u
)
üüu v
;
üüv w
}
†† 
;
†† 
}
°° 
item
££ 
.
££ 
Update
££ 
(
££ 
e
££ 
)
££ 
;
££ 
return
§§ 
item
§§ 
;
§§ 
}
•• 	
private
ßß 
void
ßß 
AppendTransferLog
ßß &
(
ßß& '
string
ßß' -
fileName
ßß. 6
,
ßß6 7
byte
ßß8 <
[
ßß< =
]
ßß= >
buffer
ßß? E
,
ßßE F
int
ßßG J
offset
ßßK Q
,
ßßQ R
int
ßßS V
count
ßßW \
)
ßß\ ]
{
®® 	
}
ÆÆ 	
private
∞∞ 
string
∞∞  
TunnelTypeToString
∞∞ )
(
∞∞) *

TunnelType
∞∞* 4

tunnelType
∞∞5 ?
)
∞∞? @
{
±± 	
switch
≤≤ 
(
≤≤ 

tunnelType
≤≤ 
)
≤≤ 
{
≥≥ 
case
¥¥ 

TunnelType
¥¥ 
.
¥¥  
Https
¥¥  %
:
¥¥% &
return
µµ 
$str
µµ "
;
µµ" #
case
∂∂ 

TunnelType
∂∂ 
.
∂∂  
	Websocket
∂∂  )
:
∂∂) *
return
∑∑ 
$str
∑∑ &
;
∑∑& '
case
∏∏ 

TunnelType
∏∏ 
.
∏∏  
Http2
∏∏  %
:
∏∏% &
return
ππ 
$str
ππ "
;
ππ" #
}
∫∫ 
return
ºº 
null
ºº 
;
ºº 
}
ΩΩ 	
private
øø 
void
øø (
ListViewSessions_OnKeyDown
øø /
(
øø/ 0
object
øø0 6
sender
øø7 =
,
øø= >
KeyEventArgs
øø? K
e
øøL M
)
øøM N
{
¿¿ 	
if
¡¡ 
(
¡¡ 
e
¡¡ 
.
¡¡ 
Key
¡¡ 
==
¡¡ 
Key
¡¡ 
.
¡¡ 
Delete
¡¡ #
)
¡¡# $
{
¬¬ 
var
√√ 

isSelected
√√ 
=
√√  
false
√√! &
;
√√& '
var
ƒƒ 
selectedItems
ƒƒ !
=
ƒƒ" #
(
ƒƒ$ %
(
ƒƒ% &
ListView
ƒƒ& .
)
ƒƒ. /
sender
ƒƒ/ 5
)
ƒƒ5 6
.
ƒƒ6 7
SelectedItems
ƒƒ7 D
;
ƒƒD E
Sessions
≈≈ 
.
≈≈ "
SuppressNotification
≈≈ -
=
≈≈. /
true
≈≈0 4
;
≈≈4 5
foreach
∆∆ 
(
∆∆ 
var
∆∆ 
item
∆∆ !
in
∆∆" $
selectedItems
∆∆% 2
.
∆∆2 3
Cast
∆∆3 7
<
∆∆7 8
SessionListItem
∆∆8 G
>
∆∆G H
(
∆∆H I
)
∆∆I J
.
∆∆J K
ToArray
∆∆K R
(
∆∆R S
)
∆∆S T
)
∆∆T U
{
«« 
if
»» 
(
»» 
item
»» 
==
»» 
SelectedSession
»»  /
)
»»/ 0

isSelected
»»1 ;
=
»»< =
true
»»> B
;
»»B C
Sessions
   
.
   
Remove
   #
(
  # $
item
  $ (
)
  ( )
;
  ) *
sessionDictionary
ÀÀ %
.
ÀÀ% &
Remove
ÀÀ& ,
(
ÀÀ, -
item
ÀÀ- 1
.
ÀÀ1 2

HttpClient
ÀÀ2 <
)
ÀÀ< =
;
ÀÀ= >
}
ÃÃ 
Sessions
ŒŒ 
.
ŒŒ "
SuppressNotification
ŒŒ -
=
ŒŒ. /
false
ŒŒ0 5
;
ŒŒ5 6
if
–– 
(
–– 

isSelected
–– 
)
–– 
SelectedSession
––  /
=
––0 1
null
––2 6
;
––6 7
}
—— 
}
““ 	
private
‘‘ 
void
‘‘ $
SelectedSessionChanged
‘‘ +
(
‘‘+ ,
)
‘‘, -
{
’’ 	
if
÷÷ 
(
÷÷ 
SelectedSession
÷÷ 
==
÷÷  "
null
÷÷# '
)
÷÷' (
{
◊◊ 
TextBoxRequest
ÿÿ 
.
ÿÿ 
Text
ÿÿ #
=
ÿÿ$ %
null
ÿÿ& *
;
ÿÿ* +
TextBoxResponse
ŸŸ 
.
ŸŸ  
Text
ŸŸ  $
=
ŸŸ% &
string
ŸŸ' -
.
ŸŸ- .
Empty
ŸŸ. 3
;
ŸŸ3 4
ImageResponse
⁄⁄ 
.
⁄⁄ 
Source
⁄⁄ $
=
⁄⁄% &
null
⁄⁄' +
;
⁄⁄+ ,
return
€€ 
;
€€ 
}
‹‹ 
const
ﬁﬁ 
int
ﬁﬁ 
truncateLimit
ﬁﬁ #
=
ﬁﬁ$ %
$num
ﬁﬁ& *
;
ﬁﬁ* +
var
‡‡ 
session
‡‡ 
=
‡‡ 
SelectedSession
‡‡ )
.
‡‡) *

HttpClient
‡‡* 4
;
‡‡4 5
var
·· 
request
·· 
=
·· 
session
·· !
.
··! "
Request
··" )
;
··) *
var
‚‚ 
fullData
‚‚ 
=
‚‚ 
(
‚‚ 
request
‚‚ #
.
‚‚# $

IsBodyRead
‚‚$ .
?
‚‚/ 0
request
‚‚1 8
.
‚‚8 9
Body
‚‚9 =
:
‚‚> ?
null
‚‚@ D
)
‚‚D E
??
‚‚F H
Array
‚‚I N
.
‚‚N O
Empty
‚‚O T
<
‚‚T U
byte
‚‚U Y
>
‚‚Y Z
(
‚‚Z [
)
‚‚[ \
;
‚‚\ ]
var
„„ 
data
„„ 
=
„„ 
fullData
„„ 
;
„„  
var
‰‰ 
	truncated
‰‰ 
=
‰‰ 
data
‰‰  
.
‰‰  !
Length
‰‰! '
>
‰‰( )
truncateLimit
‰‰* 7
;
‰‰7 8
if
ÂÂ 
(
ÂÂ 
	truncated
ÂÂ 
)
ÂÂ 
data
ÂÂ 
=
ÂÂ  !
data
ÂÂ" &
.
ÂÂ& '
Take
ÂÂ' +
(
ÂÂ+ ,
truncateLimit
ÂÂ, 9
)
ÂÂ9 :
.
ÂÂ: ;
ToArray
ÂÂ; B
(
ÂÂB C
)
ÂÂC D
;
ÂÂD E
var
ËË 
sb
ËË 
=
ËË 
new
ËË 
StringBuilder
ËË &
(
ËË& '
)
ËË' (
;
ËË( )
sb
ÈÈ 
.
ÈÈ 

AppendLine
ÈÈ 
(
ÈÈ 
$str
ÈÈ !
+
ÈÈ" #
request
ÈÈ$ +
.
ÈÈ+ ,

RequestUri
ÈÈ, 6
)
ÈÈ6 7
;
ÈÈ7 8
sb
ÍÍ 
.
ÍÍ 
Append
ÍÍ 
(
ÍÍ 
request
ÍÍ 
.
ÍÍ 

HeaderText
ÍÍ (
)
ÍÍ( )
;
ÍÍ) *
sb
ÎÎ 
.
ÎÎ 
Append
ÎÎ 
(
ÎÎ 
request
ÎÎ 
.
ÎÎ 
Encoding
ÎÎ &
.
ÎÎ& '
	GetString
ÎÎ' 0
(
ÎÎ0 1
data
ÎÎ1 5
)
ÎÎ5 6
)
ÎÎ6 7
;
ÎÎ7 8
if
ÏÏ 
(
ÏÏ 
	truncated
ÏÏ 
)
ÏÏ 
{
ÌÌ 
sb
ÓÓ 
.
ÓÓ 

AppendLine
ÓÓ 
(
ÓÓ 
)
ÓÓ 
;
ÓÓ  
sb
ÔÔ 
.
ÔÔ 
Append
ÔÔ 
(
ÔÔ 
$"
ÔÔ 
$str
ÔÔ 4
{
ÔÔ4 5
truncateLimit
ÔÔ5 B
}
ÔÔB C
$str
ÔÔC I
"
ÔÔI J
)
ÔÔJ K
;
ÔÔK L
}
 
sb
ÚÚ 
.
ÚÚ 
Append
ÚÚ 
(
ÚÚ 
(
ÚÚ 
request
ÚÚ 
as
ÚÚ !
ConnectRequest
ÚÚ" 0
)
ÚÚ0 1
?
ÚÚ1 2
.
ÚÚ2 3
ClientHelloInfo
ÚÚ3 B
)
ÚÚB C
;
ÚÚC D
TextBoxRequest
ÛÛ 
.
ÛÛ 
Text
ÛÛ 
=
ÛÛ  !
sb
ÛÛ" $
.
ÛÛ$ %
ToString
ÛÛ% -
(
ÛÛ- .
)
ÛÛ. /
;
ÛÛ/ 0
var
ıı 
response
ıı 
=
ıı 
session
ıı "
.
ıı" #
Response
ıı# +
;
ıı+ ,
fullData
ˆˆ 
=
ˆˆ 
(
ˆˆ 
response
ˆˆ  
.
ˆˆ  !

IsBodyRead
ˆˆ! +
?
ˆˆ, -
response
ˆˆ. 6
.
ˆˆ6 7
Body
ˆˆ7 ;
:
ˆˆ< =
null
ˆˆ> B
)
ˆˆB C
??
ˆˆD F
Array
ˆˆG L
.
ˆˆL M
Empty
ˆˆM R
<
ˆˆR S
byte
ˆˆS W
>
ˆˆW X
(
ˆˆX Y
)
ˆˆY Z
;
ˆˆZ [
data
˜˜ 
=
˜˜ 
fullData
˜˜ 
;
˜˜ 
	truncated
¯¯ 
=
¯¯ 
data
¯¯ 
.
¯¯ 
Length
¯¯ #
>
¯¯$ %
truncateLimit
¯¯& 3
;
¯¯3 4
if
˘˘ 
(
˘˘ 
	truncated
˘˘ 
)
˘˘ 
data
˘˘ 
=
˘˘  !
data
˘˘" &
.
˘˘& '
Take
˘˘' +
(
˘˘+ ,
truncateLimit
˘˘, 9
)
˘˘9 :
.
˘˘: ;
ToArray
˘˘; B
(
˘˘B C
)
˘˘C D
;
˘˘D E
sb
¸¸ 
=
¸¸ 
new
¸¸ 
StringBuilder
¸¸ "
(
¸¸" #
)
¸¸# $
;
¸¸$ %
sb
˝˝ 
.
˝˝ 
Append
˝˝ 
(
˝˝ 
response
˝˝ 
.
˝˝ 

HeaderText
˝˝ )
)
˝˝) *
;
˝˝* +
sb
˛˛ 
.
˛˛ 
Append
˛˛ 
(
˛˛ 
response
˛˛ 
.
˛˛ 
Encoding
˛˛ '
.
˛˛' (
	GetString
˛˛( 1
(
˛˛1 2
data
˛˛2 6
)
˛˛6 7
)
˛˛7 8
;
˛˛8 9
if
ˇˇ 
(
ˇˇ 
	truncated
ˇˇ 
)
ˇˇ 
{
ÄÄ 
sb
ÅÅ 
.
ÅÅ 

AppendLine
ÅÅ 
(
ÅÅ 
)
ÅÅ 
;
ÅÅ  
sb
ÇÇ 
.
ÇÇ 
Append
ÇÇ 
(
ÇÇ 
$"
ÇÇ 
$str
ÇÇ 4
{
ÇÇ4 5
truncateLimit
ÇÇ5 B
}
ÇÇB C
$str
ÇÇC I
"
ÇÇI J
)
ÇÇJ K
;
ÇÇK L
}
ÉÉ 
sb
ÖÖ 
.
ÖÖ 
Append
ÖÖ 
(
ÖÖ 
(
ÖÖ 
response
ÖÖ 
as
ÖÖ  "
ConnectResponse
ÖÖ# 2
)
ÖÖ2 3
?
ÖÖ3 4
.
ÖÖ4 5
ServerHelloInfo
ÖÖ5 D
)
ÖÖD E
;
ÖÖE F
if
ÜÜ 
(
ÜÜ 
SelectedSession
ÜÜ 
.
ÜÜ  
	Exception
ÜÜ  )
!=
ÜÜ* ,
null
ÜÜ- 1
)
ÜÜ1 2
{
áá 
sb
àà 
.
àà 
Append
àà 
(
àà 
Environment
àà %
.
àà% &
NewLine
àà& -
)
àà- .
;
àà. /
sb
ââ 
.
ââ 
Append
ââ 
(
ââ 
SelectedSession
ââ )
.
ââ) *
	Exception
ââ* 3
)
ââ3 4
;
ââ4 5
}
ää 
TextBoxResponse
åå 
.
åå 
Text
åå  
=
åå! "
sb
åå# %
.
åå% &
ToString
åå& .
(
åå. /
)
åå/ 0
;
åå0 1
try
éé 
{
èè 
if
êê 
(
êê 
fullData
êê 
.
êê 
Length
êê #
>
êê$ %
$num
êê& '
)
êê' (
using
ëë 
(
ëë 
var
ëë 
stream
ëë %
=
ëë& '
new
ëë( +
MemoryStream
ëë, 8
(
ëë8 9
fullData
ëë9 A
)
ëëA B
)
ëëB C
{
íí 
ImageResponse
ìì %
.
ìì% &
Source
ìì& ,
=
ìì- .
BitmapFrame
îî '
.
îî' (
Create
îî( .
(
îî. /
stream
îî/ 5
,
îî5 6!
BitmapCreateOptions
îî7 J
.
îîJ K
None
îîK O
,
îîO P
BitmapCacheOption
îîQ b
.
îîb c
OnLoad
îîc i
)
îîi j
;
îîj k
}
ïï 
}
ññ 
catch
óó 
{
òò 
ImageResponse
ôô 
.
ôô 
Source
ôô $
=
ôô% &
null
ôô' +
;
ôô+ ,
}
öö 
}
õõ 	
private
ùù 
void
ùù &
ButtonProxyOnOff_OnClick
ùù -
(
ùù- .
object
ùù. 4
sender
ùù5 ;
,
ùù; <
RoutedEventArgs
ùù= L
e
ùùM N
)
ùùN O
{
ûû 	
var
üü 
button
üü 
=
üü 
(
üü 
ToggleButton
üü &
)
üü& '
sender
üü' -
;
üü- .
if
†† 
(
†† 
button
†† 
.
†† 
	IsChecked
††  
==
††! #
true
††$ (
)
††( )
proxyServer
°° 
.
°° 
SetAsSystemProxy
°° ,
(
°°, -
(
°°- .#
ExplicitProxyEndPoint
°°. C
)
°°C D
proxyServer
°°D O
.
°°O P
ProxyEndPoints
°°P ^
[
°°^ _
$num
°°_ `
]
°°` a
,
°°a b
ProxyProtocolType
¢¢ %
.
¢¢% &
AllHttp
¢¢& -
)
¢¢- .
;
¢¢. /
else
££ 
proxyServer
§§ 
.
§§ *
RestoreOriginalProxySettings
§§ 8
(
§§8 9
)
§§9 :
;
§§: ;
}
•• 	
}
¶¶ 
}ßß ¿
_D:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.Wpf\App.xaml.cs
	namespace 	
Titanium
 
. 
Web 
. 
Proxy 
. 
Examples %
.% &
Wpf& )
{ 
public 

partial 
class 
App 
: 
Application *
{		 
}

 
} 