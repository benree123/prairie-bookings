"""Checks the portfolio demo. Run only against a disposable instance with Demo__Enabled=true."""
import os,json,urllib.request,urllib.error,http.cookiejar,datetime
base=os.environ['PRAIRIE_TEST_URL']
guest=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
anonymous=urllib.request.build_opener()
passed=0
def call(opener,path,body=None):
    headers={'Content-Type':'application/json','X-Staff-Request':'1'} if body is not None else {}
    request=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),headers=headers)
    try: response=opener.open(request,timeout=15)
    except urllib.error.HTTPError as error: response=error
    raw=response.read()
    return response.code,json.loads(raw) if raw else {}
def check(condition,name):
    global passed
    if not condition: raise AssertionError(name)
    passed+=1
    print('PASS:',name)
catalog=call(anonymous,'/api/catalog')[1]
check(catalog['demo'] is True,'Catalog reports demo mode')
check(call(anonymous,'/api/staff/session')[0]==401,'Staff data requires a session')
check(call(anonymous,'/api/staff/login',{'username':'staff','password':'wrong-password'})[0]==401,'Password login still rejects wrong passwords')
day=datetime.date.fromisoformat(catalog['today'])+datetime.timedelta(days=1)
while day.weekday()>=5: day+=datetime.timedelta(days=1)
slots=[s for s in call(anonymous,f'/api/availability?providerId=3&date={day}')[1]['slots'] if s['available']]
visitor={'providerId':3,'serviceId':1,'startsAt':slots[0]['startsAt'],'customerName':'Private Visitor','email':'private@example.com'}
check(call(anonymous,'/api/bookings',visitor)[0]==201,'Visitor can book')
check(call(anonymous,'/api/bookings',{**visitor,'startsAt':slots[1]['startsAt'],'email':'x@sample.prairiebookings.test'})[0]==400,'Visitors cannot use the sample domain')
status,session=call(guest,'/api/staff/demo',{})
check(status==200 and session['guest'],'One-click demo signs in')
check(call(guest,'/api/staff/session')[1]['guest'] is True,'Guest session persists')
query=f"/api/staff/appointments?from={catalog['today']}&to={catalog['lastDate']}&status=active"
items=call(guest,query)[1]['appointments']
check(len(items)>=9,'Sample appointments are loaded')
text=json.dumps(items)
check('Private Visitor' not in text and 'private@example.com' not in text,'Other visitors are hidden from guests')
check(any(i['customerName']=='Website visitor' for i in items),'Visitor booking appears anonymized')
sample=next(i for i in items if i['customerName']!='Website visitor')
check(call(guest,f"/api/staff/appointments/{sample['id']}/cancel",{})[0]==200,'Guest can cancel a sample')
block_slot=slots[2]['startsAt']
check(call(guest,'/api/staff/blocks',{'providerId':3,'startsAt':block_slot,'reason':'Text shown to everyone'})[0]==201,'Guest can block time')
blocks=call(guest,f'/api/staff/blocks?providerId=3&date={day}')[1]['blocks']
check(all(b['reason']=='Blocked in the demo' for b in blocks),'Guest block text is fixed')
check(next(s for s in call(anonymous,f'/api/availability?providerId=3&date={day}')[1]['slots'] if s['startsAt']==block_slot)['available'] is False,'Blocked slot is unavailable')
print(f'All {passed} demo checks passed.')
